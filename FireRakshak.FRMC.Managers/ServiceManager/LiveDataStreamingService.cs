using FireRakshak.FRMC.Core.EventAggregator;
using FireRakshak.FRMC.Core.Events;
using FireRakshak.FRMC.Core.Models;
using FireRakshak.FRMC.Host;
using FireRakshak.FRMC.Managers.SystemConfigurationManager;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FireRakshak.FRMC.Managers.ServiceManager;

/// <summary>
/// Subscribes to temperature data events and streams the live data to all connected WebSocket clients.
/// This service acts as a bridge between the DTS manager and the Data Stream server.
///
/// DUAL WEBSOCKET ARCHITECTURE:
/// - Control WebSocket (port 6164): Commands, config, health, alarms
/// - Data WebSocket (port 6165): Temperature streaming only (this service uses this)
/// </summary>
public class LiveDataStreamingService : IHostedService
{
    private readonly IEventAggregator _eventAggregator;
    private readonly ILogger<LiveDataStreamingService> _logger;
    private readonly WebSocketServer _controlServer;        // For checking if measurement is started
    private readonly IDataStreamServer _dataStreamServer;   // For broadcasting temperature data
    private readonly IConfigurationManager _configurationManager;

    private readonly ConcurrentDictionary<int, ConcurrentQueue<TemperatureDataPoint>> _zoneTemperatureWindows = new();
    private readonly TimeSpan _slidingWindowDuration = TimeSpan.FromMinutes(1);

    // Delta broadcasting: cache last-broadcast temperatures per channel to skip unchanged points.
    // Key: channelId, Value: array of temperatures indexed by decimated point index
    private readonly ConcurrentDictionary<int, double[]> _lastBroadcastTemps = new();
    // Tracks when each channel last did a full (non-delta) raw temp broadcast
    private readonly ConcurrentDictionary<int, DateTime> _lastFullTempBroadcast = new();

    // Delta thresholds for raw temperature data
    private const double RAW_TEMP_DELTA_THRESHOLD = 0.3;    // °C change needed to include point in delta
    private const int RAW_TEMP_FULL_REFRESH_SECONDS = 30;    // Force full broadcast periodically for DTSCM sync

    // Configurable decimation parameters (loaded from config, can be updated via WebSocket from DTSCM)
    // Points per km = 1000 / DtsPointResolutionMeters (e.g., at 0.4m resolution: 2500 points per km)
    private int _mediumChannelThresholdPoints = 7500;   // 3km threshold (default at 0.4m resolution)
    private int _largeChannelThresholdPoints = 15000;   // 6km threshold (default at 0.4m resolution)
    private int _decimationFactorMedium = 1;            // Default: no decimation (overridden by config)
    private int _decimationFactorLarge = 1;             // Default: no decimation (overridden by config)
    private double _pointsPerKm = 2500;                 // Default: 1000m / 0.4m = 2500 points per km
    private readonly object _decimationLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveDataStreamingService"/> class.
    /// </summary>
    public LiveDataStreamingService(
        IEventAggregator eventAggregator,
        ILogger<LiveDataStreamingService> logger,
        WebSocketServer controlServer,
        IDataStreamServer dataStreamServer,
        IConfigurationManager configurationManager)
    {
        _eventAggregator = eventAggregator;
        _logger = logger;
        _controlServer = controlServer;
        _dataStreamServer = dataStreamServer;
        _configurationManager = configurationManager;
    }



    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Live Data Streaming Service starting.");

        // Load decimation settings from persisted configuration
        try
        {
            var sysConfig = await _configurationManager.GetSystemConfigurationAsync();
            if (sysConfig != null)
            {
                lock (_decimationLock)
                {
                    // Calculate points per km based on configured DTS resolution
                    double pointResolution = sysConfig.DtsPointResolutionMeters > 0
                        ? sysConfig.DtsPointResolutionMeters
                        : 0.4;
                    _pointsPerKm = 1000.0 / pointResolution;

                    _mediumChannelThresholdPoints = (int)(sysConfig.DecimationMediumThresholdKm * _pointsPerKm);
                    _largeChannelThresholdPoints = (int)(sysConfig.DecimationLargeThresholdKm * _pointsPerKm);
                    _decimationFactorMedium = Math.Max(1, sysConfig.DecimationMediumFactor);
                    _decimationFactorLarge = Math.Max(1, sysConfig.DecimationLargeFactor);
                }

                _logger.LogInformation(
                    "Loaded decimation config: Medium >= {MediumKm}km (1:{MediumFactor}), Large >= {LargeKm}km (1:{LargeFactor}), Resolution={Resolution}m ({PointsPerKm} pts/km)",
                    sysConfig.DecimationMediumThresholdKm, _decimationFactorMedium,
                    sysConfig.DecimationLargeThresholdKm, _decimationFactorLarge,
                    sysConfig.DtsPointResolutionMeters, _pointsPerKm);
                _logger.LogWarning(
                    "DECIMATION ACTIVE VALUES: mediumThresholdPts={MediumPts}, largeThresholdPts={LargePts}, " +
                    "mediumFactor={MediumFactor}, largeFactor={LargeFactor}, pointsPerKm={PtsPerKm}",
                    _mediumChannelThresholdPoints, _largeChannelThresholdPoints,
                    _decimationFactorMedium, _decimationFactorLarge, _pointsPerKm);
            }
            else
            {
                _logger.LogWarning(
                    "System config was NULL — using hardcoded defaults: mediumThresholdPts={MediumPts}, " +
                    "largeThresholdPts={LargePts}, mediumFactor={MediumFactor}, largeFactor={LargeFactor}",
                    _mediumChannelThresholdPoints, _largeChannelThresholdPoints,
                    _decimationFactorMedium, _decimationFactorLarge);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to load decimation config, using defaults: mediumFactor={MediumFactor}, largeFactor={LargeFactor}",
                _decimationFactorMedium, _decimationFactorLarge);
        }

        // Subscribe to the TemperatureDataReceivedEvent when the service starts.
        _eventAggregator.Subscribe<TemperatureDataReceivedEvent>(HandleTemperatureDataReceivedEvent);
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Live Data Streaming Service stopping.");
        // No explicit unsubscription needed for singletons unless resource cleanup is critical.
        return Task.CompletedTask;
    }



    /// <summary>

    /// Retrieves the current temperature data for a specific zone within the sliding window.

    /// </summary>

    /// <param name="zoneId">The ID of the zone.</param>

    /// <returns>An array of TemperatureDataPoint within the sliding window.</returns>

    public TemperatureDataPoint[] GetZoneLiveData(int zoneId)

    {

        if (_zoneTemperatureWindows.TryGetValue(zoneId, out var queue))

        {

            return queue.ToArray();

        }

        return Array.Empty<TemperatureDataPoint>();

    }



    /// <summary>
    /// Updates the decimation configuration. Called when DTSCM sends new settings.
    /// Also persists the settings to SystemConfiguration.json for survival across restarts.
    /// </summary>
    /// <param name="mediumThresholdKm">Channel length threshold for medium decimation (km)</param>
    /// <param name="largeThresholdKm">Channel length threshold for large decimation (km)</param>
    /// <param name="mediumFactor">Decimation factor for medium channels (e.g., 5 = take 1 in 5)</param>
    /// <param name="largeFactor">Decimation factor for large channels (e.g., 10 = take 1 in 10)</param>
    public async Task UpdateDecimationConfigAsync(double mediumThresholdKm, double largeThresholdKm, int mediumFactor, int largeFactor)
    {
        lock (_decimationLock)
        {
            // Convert km to points using configured resolution (points per km calculated at startup)
            _mediumChannelThresholdPoints = (int)(mediumThresholdKm * _pointsPerKm);
            _largeChannelThresholdPoints = (int)(largeThresholdKm * _pointsPerKm);
            _decimationFactorMedium = Math.Max(1, mediumFactor);
            _decimationFactorLarge = Math.Max(1, largeFactor);

            _logger.LogInformation(
                "Decimation config updated: Medium >= {MediumKm}km (1 in {MediumFactor}), Large >= {LargeKm}km (1 in {LargeFactor}), using {PointsPerKm} pts/km",
                mediumThresholdKm, _decimationFactorMedium, largeThresholdKm, _decimationFactorLarge, _pointsPerKm);
        }

        // Persist to configuration file
        try
        {
            var sysConfig = await _configurationManager.GetSystemConfigurationAsync();
            if (sysConfig != null)
            {
                sysConfig.DecimationMediumThresholdKm = mediumThresholdKm;
                sysConfig.DecimationLargeThresholdKm = largeThresholdKm;
                sysConfig.DecimationMediumFactor = mediumFactor;
                sysConfig.DecimationLargeFactor = largeFactor;

                await _configurationManager.UpdateSystemConfigurationAsync(sysConfig, "System");
                _logger.LogInformation("Decimation config persisted to SystemConfiguration.json");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist decimation config");
        }
    }

    /// <summary>
    /// Gets the current decimation configuration.
    /// </summary>
    public (double MediumThresholdKm, double LargeThresholdKm, int MediumFactor, int LargeFactor) GetDecimationConfig()
    {
        lock (_decimationLock)
        {
            return (
                _mediumChannelThresholdPoints / _pointsPerKm,
                _largeChannelThresholdPoints / _pointsPerKm,
                _decimationFactorMedium,
                _decimationFactorLarge
            );
        }
    }

    /// <summary>
    /// Handles the TemperatureDataReceivedEvent by processing the data and broadcasting it.
    /// For large channels, decimates data to reduce bandwidth and prevent health status delays.
    /// Decimation thresholds and factors are configurable from DTSCM.
    ///
    /// DUAL WEBSOCKET: Uses DataStreamServer (port 6165) for broadcasting temperature data,
    /// keeping the control WebSocket (port 6164) free for commands and health status.
    /// </summary>
    private async Task HandleTemperatureDataReceivedEvent(TemperatureDataReceivedEvent @event)
    {
        // Block live data until Start Live Measurement (checked via control server)
        if (!_controlServer.IsStreamingActive)
        {
            _logger.LogDebug(
                "Live data blocked – measurement not started. Channel={ChannelId}",
                @event.ChannelId);
            return;
        }

        // Also check if data stream server is active
        if (!_dataStreamServer.IsStreamingActive)
        {
            _logger.LogDebug(
                "Live data blocked – no data stream clients connected. Channel={ChannelId}",
                @event.ChannelId);
            return;
        }

        try
        {
            // Validate data before broadcasting
            if (@event.Data == null || @event.Data.Length == 0)
            {
                _logger.LogDebug("No temperature data to broadcast for channel {ChannelId}", @event.ChannelId);
                return;
            }

            // Get current decimation settings (thread-safe)
            int mediumThreshold, largeThreshold, mediumFactor, largeFactor;
            lock (_decimationLock)
            {
                mediumThreshold = _mediumChannelThresholdPoints;
                largeThreshold = _largeChannelThresholdPoints;
                mediumFactor = _decimationFactorMedium;
                largeFactor = _decimationFactorLarge;
            }

            // Decimate data for large channels to reduce bandwidth
            // This prevents health status delays when streaming large amounts of data
            var dataToSend = @event.Data;
            int decimationFactor = 1;

            if (@event.Data.Length >= largeThreshold)
            {
                decimationFactor = largeFactor;
            }
            else if (@event.Data.Length > mediumThreshold)
            {
                decimationFactor = mediumFactor;
            }

            if (decimationFactor > 1)
            {
                dataToSend = @event.Data
                    .Where((_, index) => index % decimationFactor == 0)
                    .ToArray();

                _logger.LogDebug(
                    "Decimated channel {ChannelId} data: {OriginalCount} -> {DecimatedCount} points (1 in {Factor})",
                    @event.ChannelId, @event.Data.Length, dataToSend.Length, decimationFactor);
            }
            else
            {
                _logger.LogDebug(
                    "Broadcasting full data for channel {ChannelId}: {PointCount} points",
                    @event.ChannelId, dataToSend.Length);
            }

            // Delta broadcasting: compare to last-broadcast temperatures and only send changed points.
            // In steady state most points change by < 0.1°C, so this cuts 80-90% of bandwidth.
            // Binary protocol: ~91% smaller than JSON + ~98% less CPU for serialize/parse.
            var lastFullRefresh = _lastFullTempBroadcast.GetOrAdd(@event.ChannelId, DateTime.MinValue);
            bool isFullRefresh = (DateTime.UtcNow - lastFullRefresh).TotalSeconds >= RAW_TEMP_FULL_REFRESH_SECONDS;
            var now = DateTime.UtcNow;

            if (!isFullRefresh && _lastBroadcastTemps.TryGetValue(@event.ChannelId, out var cachedTemps)
                && cachedTemps.Length == dataToSend.Length)
            {
                // Build delta: collect (index, temperature) for changed points
                var changedPoints = new List<(ushort Index, float Temperature)>();
                for (int i = 0; i < dataToSend.Length; i++)
                {
                    if (Math.Abs(dataToSend[i].Temperature - cachedTemps[i]) >= RAW_TEMP_DELTA_THRESHOLD)
                    {
                        changedPoints.Add(((ushort)i, (float)dataToSend[i].Temperature));
                    }
                }

                if (changedPoints.Count == 0)
                {
                    _logger.LogTrace(
                        "Raw temp broadcast skipped for channel {ChannelId} - no points changed (delta mode)",
                        @event.ChannelId);
                    return; // Nothing changed — skip broadcast entirely
                }

                _logger.LogDebug(
                    "Delta raw temp broadcast for channel {ChannelId}: {Changed}/{Total} points changed (binary)",
                    @event.ChannelId, changedPoints.Count, dataToSend.Length);

                // Binary delta: 17 + 6*N bytes (vs ~45*N bytes JSON)
                var binaryData = BinaryProtocol.SerializeDeltaTemperatureData(
                    @event.ChannelId, now, changedPoints);
                await _dataStreamServer.BroadcastBinaryToChannelSubscribersAsync(@event.ChannelId, binaryData);
            }
            else
            {
                // Full broadcast (first time, refresh cycle, or array length changed)
                // Extract start position and spacing for implicit position reconstruction
                double startPos = dataToSend.Length > 0 ? dataToSend[0].Position : 0;
                double spacing = dataToSend.Length > 1
                    ? dataToSend[1].Position - dataToSend[0].Position
                    : 0.4; // default resolution

                _logger.LogDebug(
                    "Full raw temp broadcast for channel {ChannelId}: {Count} points (binary, refresh)",
                    @event.ChannelId, dataToSend.Length);

                // Binary full: 33 + 4*N bytes (vs ~45*N bytes JSON)
                var binaryData = BinaryProtocol.SerializeFullTemperatureData(
                    @event.ChannelId, now, startPos, spacing, dataToSend, 0, dataToSend.Length);
                await _dataStreamServer.BroadcastBinaryToChannelSubscribersAsync(@event.ChannelId, binaryData);

                _lastFullTempBroadcast[@event.ChannelId] = now;
            }

            // Update cache with current temperatures (after successful broadcast)
            var newCache = new double[dataToSend.Length];
            for (int i = 0; i < dataToSend.Length; i++)
                newCache[i] = dataToSend[i].Temperature;
            _lastBroadcastTemps[@event.ChannelId] = newCache;
        }
        catch (Exception ex)
        {
            // CRITICAL FIX: Prevent crashes from propagating and killing FRMC
            _logger.LogError(ex, "Failed to broadcast temperature data for channel {ChannelId}", @event.ChannelId);
        }
    }

}
