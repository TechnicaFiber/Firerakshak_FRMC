using FireRakshak.FRMC.Core.Enums;
using FireRakshak.FRMC.Core.EventAggregator;
using FireRakshak.FRMC.Core.Events;
using FireRakshak.FRMC.Core.Memory;
using FireRakshak.FRMC.Core.Models;
using FireRakshak.FRMC.Host;
using FireRakshak.FRMC.Managers.SystemConfigurationManager;
using FireRakshak.FRMC.Managers.SystemHealthManager;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;


namespace FireRakshak.FRMC.Managers.DtsManager;

/// <summary>
/// Manages all operations related to the Distributed Temperature Sensing (DTS) device.
/// This class is responsible for initiating temperature reads, fetching health data, and sending configuration commands.
/// It uses a protocol handler to communicate with the hardware and publishes events to the rest of the application.
/// </summary>
public class DtsManager : IDtsManager, IDisposable
{
    private readonly IEventAggregator _eventAggregator;
    private readonly ILogger<DtsManager> _logger;
    private readonly DtsProtocolHandler _protocolHandler; // Handles the low-level communication protocol with the DTS device.
    private readonly IConfigurationManager _configurationManager;
    private readonly IServiceProvider _serviceProvider;
    private Timer? _fiberBreakCheckTimer; // Timer for periodic fiber break checks
    private Timer? _healthCheckTimer; // Timer for periodic health parameter checks
    private readonly SemaphoreSlim _fiberBreakCheckSemaphore = new(1, 1); // Prevent concurrent fiber break checks
    private readonly SemaphoreSlim _healthCheckSemaphore = new(1, 1); // Prevent concurrent health checks
    private const int FIBER_BREAK_CHECK_INTERVAL_MS = 60000; // 1 minute
    private const int HEALTH_CHECK_INTERVAL_MS = 120000; // 2 minutes
    private const double DEFAULT_LASER_ON_CURRENT_MA = 90.0; // Default pump current when turning laser on
    private HealthParameters? _lastHealthSnapshot;

    private readonly SemaphoreSlim _connectSemaphore = new(1, 1);

    private bool _disposed = false;

    // Track previous fiber break state to avoid duplicate events
    private readonly Dictionary<int, (bool isBroken, double position)> _previousFiberBreakState = new();

    public DateTime Timestamp { get; set; }

    // ADDED: Track DTS responsiveness for disconnect detection
    private DateTime _lastSuccessfulRead = DateTime.MinValue;
    private bool _dtsCurrentlyResponsive = false;
    private const int STALE_DATA_THRESHOLD_SECONDS = 60;

    /// <inheritdoc/>
    public bool IsConnected => _protocolHandler.IsConnected;
    private const int BaseDiscardedCount = 475;
    private const double DefaultPointResolution = 0.4; // Default if not configured

    // MEMORY OPTIMIZATION: Pooled buffers for temperature data arrays
    // Eliminates per-scan allocations by reusing arrays across scans
    private readonly ChannelDataBufferPool _bufferPool = new();


    /// <summary>
    /// Initializes a new instance of the <see cref="DtsManager"/> class.
    /// </summary>
    /// <param name="eventAggregator">The event aggregator used to publish system-wide events.</param>
    /// <param name="logger">The logger for recording operational information and errors.</param>
    /// <param name="protocolHandler">The handler responsible for the actual communication with the DTS device.</param>
    public DtsManager(IEventAggregator eventAggregator, ILogger<DtsManager> logger, DtsProtocolHandler protocolHandler, IServiceProvider serviceProvider, IConfigurationManager configurationManager)
    {
        _eventAggregator = eventAggregator;
        _logger = logger;
        _protocolHandler = protocolHandler;
        _serviceProvider = serviceProvider;
        _configurationManager = configurationManager;
        // Start fiber break monitoring timer (1-minute interval)
        _fiberBreakCheckTimer = new Timer(
            CheckFiberBreakAlarms,
            null,
            TimeSpan.FromSeconds(30), // Start after 30 seconds
            TimeSpan.FromMilliseconds(FIBER_BREAK_CHECK_INTERVAL_MS));

        // Start health parameter monitoring timer (2-minute interval)
        // Start immediately (5 seconds) to establish DTS communication status quickly
        _healthCheckTimer = new Timer(
            CheckHealthParameters,
            null,
            TimeSpan.FromSeconds(5), // Start after 5 seconds (immediate health check)
            TimeSpan.FromMilliseconds(HEALTH_CHECK_INTERVAL_MS));

        // Subscribe to laser control events
        _eventAggregator.Subscribe<LaserControlRequestedEvent>(HandleLaserControlRequestedEvent);

        _logger.LogInformation("DtsManager initialized with fiber break monitoring (30s start) and health monitoring (5s start, 2-min interval)");
        _configurationManager = configurationManager;
    }

    /// <inheritdoc/>
    public Task ConnectAsync()
    {
        return _protocolHandler.ConnectAsync();
    }

    /// <inheritdoc/>
    public Task DisconnectAsync()
    {
        _protocolHandler.Disconnect();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void UpdateDtsConnection(string ip, int port)
    {
        _protocolHandler.UpdateDtsConnection(ip, port);
    }

    private async Task EnsureConnectedAsync()
    {
        if (IsConnected)
            return;

        await _connectSemaphore.WaitAsync();
        try
        {
            if (IsConnected)
                return;

            _logger.LogInformation("DTS not connected. Connecting...");

            await _protocolHandler.ConnectAsync();

            await _eventAggregator.PublishAsync(
                new HealthParameterChangedEvent(
                    HealthParameter.DtsResponsive,
                    true,
                    DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DTS connection failed");

            await _eventAggregator.PublishAsync(
                new HealthParameterChangedEvent(
                    HealthParameter.DtsResponsive,
                    false,
                    DateTime.UtcNow));

            throw; // fail fast, let caller decide
        }
        finally
        {
            _connectSemaphore.Release();
        }
    }

    public void ForceDisconnect()
    {
        _protocolHandler?.Disconnect();
    }

    /// <summary>
    /// Reads raw temperature data from a DTS channel without parsing.
    /// Used by the pipeline for parallel processing - TCP read is sequential, parsing is parallel.
    /// </summary>
    /// <param name="channel">The channel ID (0-based DTS protocol channel).</param>
    /// <returns>Raw response bytes, or null if read fails.</returns>
    public async Task<byte[]?> ReadChannelRawDataAsync(byte channel)
    {
        try
        {
            await EnsureConnectedAsync();
            var command = DtsCommandBuilder.BuildReadTemperatureCommand(channel);
            var response = await _protocolHandler.SendCommandAndReceiveResponseAsync(command);

            // Track successful read
            _lastSuccessfulRead = DateTime.UtcNow;

            // If DTS was unresponsive, mark it as responsive again
            if (!_dtsCurrentlyResponsive)
            {
                _dtsCurrentlyResponsive = true;
                await _eventAggregator.PublishAsync(
                    new HealthParameterChangedEvent(
                        Core.Enums.HealthParameter.DtsResponsive,
                        true,
                        DateTime.UtcNow));
                _logger.LogInformation("DTS connection restored (raw read)");
            }

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read raw data from channel {Channel}", channel);

            // Check if DTS has been unresponsive for too long
            var timeSinceLastRead = DateTime.UtcNow - _lastSuccessfulRead;

            if (timeSinceLastRead.TotalSeconds > STALE_DATA_THRESHOLD_SECONDS && _dtsCurrentlyResponsive)
            {
                _dtsCurrentlyResponsive = false;

                await _eventAggregator.PublishAsync(
                    new HealthParameterChangedEvent(
                        Core.Enums.HealthParameter.DtsResponsive,
                        false,
                        DateTime.UtcNow));

                _logger.LogCritical("DTS OFFLINE: No data received for {Seconds} seconds", timeSinceLastRead.TotalSeconds);
            }

            return null;
        }
    }

    /// <inheritdoc/>
    /// FIXED: Added disconnect detection - publishes DTS unresponsive event if reads fail
    public async Task<TemperatureDataPoint[]> ReadChannelTemperatureAsync(byte channel)
    {
        try
        {
            await EnsureConnectedAsync();
            var command = DtsCommandBuilder.BuildReadTemperatureCommand(channel);

            var response = await _protocolHandler.SendCommandAndReceiveResponseAsync(command);

            // Convert from 0-based DTS protocol channel to 1-based FRMC channel ID for the event
            int frmcChannelId = channel + 1;

            var temperatureData = await ParseTemperatureResponseAsync(response, frmcChannelId);

            // Filter temperature data based on configured channel length
            var channelConfig = await _configurationManager.GetChannelConfigurationAsync(frmcChannelId);
            //var CorrectionLength = channelConfig?.CorrectionLength ?? 0;
            if (channelConfig != null && channelConfig.ChannelLength > 0)
            {
                double configuredLength = channelConfig.ChannelLength;
                temperatureData = temperatureData
                    .Where(p => p.Position <= configuredLength)
                    .ToArray();

                _logger.LogDebug(
                    "Channel {ChannelId}: Filtered temperature data to {Count} points within configured length of {Length}m",
                    frmcChannelId, temperatureData.Length, configuredLength);
            }


            // Check if returned data is entirely standby/idle (-273.1°C) when laser is not running
            bool isStandby = temperatureData.Length > 0 && temperatureData.All(p => p.Temperature < -200);
            if (!isStandby)
            {
                // Publish an event with the filtered temperature data using FRMC channel ID
                await _eventAggregator.PublishAsync(new TemperatureDataReceivedEvent(frmcChannelId, temperatureData, DateTime.UtcNow));
            }
            else
            {
                _logger.LogDebug("Channel {ChannelId}: DTS returned standby data (-273.1°C); skipping TemperatureDataReceivedEvent to protect channel cache.", frmcChannelId);
            }

            // ADDED: Track successful read
            _lastSuccessfulRead = DateTime.UtcNow;

            // ADDED: If DTS was unresponsive, mark it as responsive again
            if (!_dtsCurrentlyResponsive)
            {
                _dtsCurrentlyResponsive = true;
                await _eventAggregator.PublishAsync(
                    new HealthParameterChangedEvent(
                        Core.Enums.HealthParameter.DtsResponsive,
                        true,
                        DateTime.UtcNow));
                _logger.LogInformation("✓ DTS connection restored");
            }

            return temperatureData;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read temperature from channel {Channel}", channel);

            // ADDED: Check if DTS has been unresponsive for too long
            var timeSinceLastRead = DateTime.UtcNow - _lastSuccessfulRead;

            if (timeSinceLastRead.TotalSeconds > STALE_DATA_THRESHOLD_SECONDS && _dtsCurrentlyResponsive)
            {
                _dtsCurrentlyResponsive = false;

                // Publish DTS unresponsive event
                await _eventAggregator.PublishAsync(
                    new HealthParameterChangedEvent(
                        Core.Enums.HealthParameter.DtsResponsive,
                        false,
                        DateTime.UtcNow));

                _logger.LogCritical("⚠️ DTS OFFLINE: No data received for {Seconds} seconds", timeSinceLastRead.TotalSeconds);
            }

            // Return empty array instead of throwing
            return Array.Empty<TemperatureDataPoint>();
        }
    }

    /// <summary>
    /// Parses DTS temperature response bytes into TemperatureDataPoint array.
    /// OPTIMIZED: Uses ArrayPool to eliminate per-scan array allocations.
    /// OPTIMIZED: Uses direct byte manipulation to eliminate 25,000 byte[] allocations per channel.
    /// FIXED: Converted to async to avoid blocking on GetSystemConfigurationAsync.
    /// </summary>
    /// <param name="response">Raw DTS response bytes</param>
    /// <param name="channelId">Channel ID (1-based) for buffer pool management</param>
    private async Task<TemperatureDataPoint[]> ParseTemperatureResponseAsync(byte[] response, int channelId)
    {
        try
        {
            // As per the TCP-DTS PDF document:
            // Response: FLAG2 (2) + LEN (4) + ADR (1) + CMD (1) + CH (1) + ID (4) + T_DATA (N*2) + SUM (1)
            const int minResponseLength = 14;

            if (response == null || response.Length < minResponseLength)
            {
                _logger.LogWarning("Invalid temperature response: too short.");
                return Array.Empty<TemperatureDataPoint>();
            }

            // Checksum validation
            byte receivedChecksum = response[response.Length - 1];
            byte calculatedChecksum = 0;
            for (int i = 0; i < response.Length - 1; i++)
            {
                calculatedChecksum += response[i];
            }

            if (receivedChecksum != calculatedChecksum)
            {
                _logger.LogWarning("Invalid checksum. Received: {ReceivedChecksum}, Calculated: {CalculatedChecksum}", receivedChecksum, calculatedChecksum);
                return Array.Empty<TemperatureDataPoint>();
            }

            // FLAG2 validation
            if (response[0] != 0xCC || response[1] != 0xAA)
            {
                _logger.LogWarning("Invalid response header (FLAG2).");
                return Array.Empty<TemperatureDataPoint>();
            }

            // FIXED: Use await instead of blocking .GetResult()
            var systemConfig = await _configurationManager.GetSystemConfigurationAsync();
            double correctionLength = systemConfig?.CorrectionLength ?? 0;
            double pointResolution = systemConfig?.DtsPointResolutionMeters ?? DefaultPointResolution;

            int _extDis = (int)(correctionLength / pointResolution);
            int _discardedCount = BaseDiscardedCount + _extDis;

            // Each temperature = 2 bytes
            int totalTempCount = (response.Length - minResponseLength) / 2;
            const int discardCount = 475; // discard first 473 samples

            if (totalTempCount <= discardCount)
            {
                _logger.LogWarning("Not enough samples ({Count}) to discard {Discard}.", totalTempCount, discardCount);
                return Array.Empty<TemperatureDataPoint>();
            }

            int validCount = totalTempCount - discardCount;

            // MEMORY OPTIMIZATION: Use pooled buffer instead of allocating new array
            // The buffer pool automatically:
            // - Returns the previous scan's array to the pool
            // - Provides an appropriately-sized array for current scan
            // - Auto-sizes when channel length changes (ArrayPool buckets)
            var dataPoints = _bufferPool.GetBuffer(channelId, validCount);

            // Use configurable point resolution from system config
            double distanceResolution = pointResolution;

            // MEMORY OPTIMIZATION: Use direct byte manipulation to avoid 25,000 byte[2] allocations per channel
            // Before: new byte[2] + Array.Copy + Array.Reverse = ~26 bytes allocation per sample
            // After: Direct big-endian read = 0 allocations
            // Note: Can't use Span in async methods (C# 10), so using direct byte access

            for (int i = 0; i < validCount; i++)
            {
                int sampleIndex = discardCount + i;
                int dataIndex = 13 + (sampleIndex * 2);

                // Guard against malformed responses
                if (dataIndex + 1 >= response.Length - 1) // -1 for checksum
                {
                    _logger.LogWarning("Unexpected response length while parsing sample {SampleIndex}.", sampleIndex);
                    break;
                }

                // OPTIMIZED: Temperature is a 16-bit signed integer (big-endian)
                // Direct byte manipulation - zero allocations
                short rawTemp = (short)((response[dataIndex] << 8) | response[dataIndex + 1]);
                double temperature = rawTemp / 10.0;

                // Compute position with correction length applied
                // Positions in the correction length area will be negative and excluded from zones
                double position = (i * distanceResolution) - correctionLength;

                dataPoints[i] = new TemperatureDataPoint(temperature, position);
            }

            // Return a properly-sized array segment
            // Note: Pooled array may be larger than validCount, so we return exact size
            if (dataPoints.Length == validCount)
            {
                return dataPoints;
            }
            else
            {
                // ArrayPool returns power-of-2 sizes, so copy to exact size for downstream consumers
                var result = new TemperatureDataPoint[validCount];
                Array.Copy(dataPoints, result, validCount);
                return result;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing DTS temperature response.");
        }

        return Array.Empty<TemperatureDataPoint>();
    }


    /// <summary>
    /// Timer callback to check health parameters.
    /// FIXED: Synchronous wrapper for timer to avoid async void.
    /// </summary>
    private void CheckHealthParameters(object? state)
    {
        // Fire and forget with proper exception handling
        _ = CheckHealthParametersAsync();
    }

    /// <summary>
    /// Async method to check health parameters.
    /// Protected by semaphore to prevent concurrent execution.
    /// FIXED: Converted from async void to async Task for proper exception handling.
    /// </summary>
    private async Task CheckHealthParametersAsync()
    {
        // Non-blocking check - skip if previous check is still running
        if (!await _healthCheckSemaphore.WaitAsync(0))
        {
            _logger.LogWarning("Health parameter check still running, skipping this cycle");
            return;
        }

        try
        {
            await ReadHealthParametersAsync();
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "CRITICAL: Health parameter check failed - DTS may be offline");

            // Publish DTS unresponsive event
            try
            {
                await _eventAggregator.PublishAsync(
                    new HealthParameterChangedEvent(
                        Core.Enums.HealthParameter.DtsResponsive,
                        false,
                        DateTime.UtcNow));
            }
            catch (Exception eventEx)
            {
                _logger.LogError(eventEx, "Failed to publish DTS unresponsive event");
            }
        }
        finally
        {
            _healthCheckSemaphore.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<HealthParameters> ReadHealthParametersAsync()
    {
        _logger.LogInformation("Reading DTS health parameters from device...");

        try
        {
            // Read module temperature
            double moduleTemperature = await ReadModuleTemperatureAsync();

            // Read DFB laser parameters
            var (laserCurrent, lampStatus) = await ReadDfbLaserParametersAsync();

            // Read overall parameters (pump current and APD voltage)
            var (pumpCurrent, apdValue) = await ReadOverallParametersAsync();

            var health = new HealthParameters(
                lampStatus: lampStatus,
                dtsResponsive: true,
                moduleTemperature: moduleTemperature,
                apdValue: apdValue,
                laserCurrent: laserCurrent,
                pumpCurrent: pumpCurrent,
                lastCheck: DateTime.UtcNow
            );

            var systemHealthManager = _serviceProvider.GetRequiredService<ISystemHealthManager>();
            await systemHealthManager.UpdateHealthParametersAsync(health);

            // Publish individual parameter change events
            await _eventAggregator.PublishAsync(
                new HealthParameterChangedEvent(Core.Enums.HealthParameter.DtsResponsive, health.DtsResponsive, health.LastCheck));
            await _eventAggregator.PublishAsync(
                new HealthParameterChangedEvent(Core.Enums.HealthParameter.ModuleTemperature, health.ModuleTemperature, health.LastCheck));

            await _eventAggregator.PublishAsync(
                new HealthParameterChangedEvent(Core.Enums.HealthParameter.LaserCurrent, health.LaserCurrent, health.LastCheck));

            //_logger.LogWarning("###PERIODIC HEALTH### publishing PumpCurrent={Current}mA (this controls _laser state)",
            //    health.PumpCurrent);
            await _eventAggregator.PublishAsync(
                new HealthParameterChangedEvent(Core.Enums.HealthParameter.PumpCurrent, health.PumpCurrent, health.LastCheck));
            await _eventAggregator.PublishAsync(
                new HealthParameterChangedEvent(Core.Enums.HealthParameter.ApdValue, health.ApdValue, health.LastCheck));
            await _eventAggregator.PublishAsync(
                new HealthParameterChangedEvent(Core.Enums.HealthParameter.LampStatus, health.LampStatus, health.LastCheck));

            bool hasChanged = _lastHealthSnapshot == null ||
                  _lastHealthSnapshot.ModuleTemperature != health.ModuleTemperature ||
                  _lastHealthSnapshot.LaserCurrent != health.LaserCurrent ||
                  _lastHealthSnapshot.PumpCurrent != health.PumpCurrent ||
                  _lastHealthSnapshot.ApdValue != health.ApdValue ||
                  _lastHealthSnapshot.LampStatus != health.LampStatus ||
                  _lastHealthSnapshot.DtsResponsive != health.DtsResponsive;

            if (hasChanged)
            {
                _logger.LogInformation(
                    "DTS health changed: Temp={Temp}°C, Laser={Laser}mA, Pump={Pump}mA, APD={Apd}mV, Lamp={Lamp}, Responsive={Resp}",
                    health.ModuleTemperature,
                    health.LaserCurrent,
                    health.PumpCurrent,
                    health.ApdValue,
                    health.LampStatus,
                    health.DtsResponsive);

                _lastHealthSnapshot = health;
            }


            return health;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading DTS health parameters");

            // Mark DTS as not responsive
            var health = new HealthParameters(
                lampStatus: false,
                dtsResponsive: false,
                moduleTemperature: 0,
                apdValue: 0,
                laserCurrent: 0,
                pumpCurrent: 0,
                lastCheck: DateTime.UtcNow
            );

            await _eventAggregator.PublishAsync(
                new HealthParameterChangedEvent(Core.Enums.HealthParameter.DtsResponsive, false, DateTime.UtcNow));

            return health;
        }
    }




    /// <inheritdoc/>
    public Task ConfigureChannelLengthAsync(byte channel, double length)
    {
        _logger.LogInformation("Configuring channel {ChannelId} length to {Length}", channel, length);

        // TODO: Implement the actual command to configure the channel length on the DTS device
        // via the _protocolHandler. This might involve sending a specific command packet.

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<bool> ConfigureFromJsonAsync()
    {
        var config = await _configurationManager.LoadDtsConfigurationAsync();
        return await ConfigureDtsParametersAsync(config);
    }
    /// <inheritdoc/>
    public async Task<bool> ConfigureDtsParametersAsync(DtsConfiguration config)
    {
        _logger.LogInformation(
            "Configuring DTS parameters: PulseWidth={PulseWidth}, PumpCurrent={PumpCurrent}",
            config.PulseWidth,
            config.PumpCurrent
        );

        try
        {
            await EnsureConnectedAsync();

            // Set pump current
            if (!await SetPumpCurrentAsync(config.PumpCurrent))
                return false;

            // Set pulse width
            if (!await SetPulseWidthAsync(config.PulseWidth))
                return false;

            _logger.LogInformation("DTS configuration applied successfully");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to configure DTS parameters");
            return false;
        }
    }

    /// <summary>
    /// Timer callback to check fiber break alarms for all channels.
    /// FIXED: Synchronous wrapper for timer to avoid async void.
    /// </summary>
    private void CheckFiberBreakAlarms(object? state)
    {
        // Fire and forget with proper exception handling
        _ = CheckFiberBreakAlarmsAsync();
    }

    /// <summary>
    /// Async method to check fiber break alarms.
    /// Protected by semaphore to prevent concurrent execution.
    /// FIXED: Converted from async void to async Task for proper exception handling.
    /// </summary>
    private async Task CheckFiberBreakAlarmsAsync()
    {
        // Non-blocking check - skip if previous check is still running
        if (!await _fiberBreakCheckSemaphore.WaitAsync(0))
        {
            _logger.LogWarning("Fiber break check still running, skipping this cycle");
            return;
        }

        try
        {
            await ReadFiberBreakAlarmAsync();
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "CRITICAL: Fiber break check failed - DTS may be offline");

            // Publish DTS unresponsive event
            try
            {
                await _eventAggregator.PublishAsync(
                    new HealthParameterChangedEvent(
                        Core.Enums.HealthParameter.DtsResponsive,
                        false,
                        DateTime.UtcNow));
            }
            catch (Exception eventEx)
            {
                _logger.LogError(eventEx, "Failed to publish DTS unresponsive event");
            }
        }
        finally
        {
            _fiberBreakCheckSemaphore.Release();
        }
    }

    /// <summary>
    /// Reads fiber break alarm status for all channels using command 0x0D.
    /// </summary>
    public async Task ReadFiberBreakAlarmAsync()
    {
        _logger.LogInformation("Reading fiber break alarm status for all channels");

        try
        {
            await EnsureConnectedAsync();
            var command = DtsCommandBuilder.BuildReadFiberBreakAlarmCommand();
            var response = await _protocolHandler.SendCommandAndReceiveResponseAsync(command);

            await ParseFiberBreakResponse(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading fiber break alarm");
        }
    }

    /// <summary>
    /// Parses the fiber break alarm response and publishes events for each channel.
    /// Response format: FLAG2 (2) + LEN (4) + ADR (1) + CMD (1) + ACH1...ACH16 (48) + SUM (1)
    /// Each ACH is 3 bytes: alarm status (1=broken, 0=normal) + position high byte + position low byte.
    /// Position in meters = (high * 256 + low) * 0.4
    /// </summary>
    private async Task ParseFiberBreakResponse(byte[] response)
    {
        try
        {
            const int expectedLength = 57;
            const int minResponseLength = 8;
            const int channelDataStartIndex = 8;
            const int bytesPerChannel = 3;
            const int numberOfChannels = 16;

            if (response == null || response.Length < minResponseLength)
            {
                _logger.LogWarning("Invalid fiber break response: too short (length={Length})", response?.Length ?? 0);
                return;
            }

            byte receivedChecksum = response[response.Length - 1];
            byte calculatedChecksum = 0;
            for (int i = 0; i < response.Length - 1; i++)
            {
                calculatedChecksum += response[i];
            }

            if (receivedChecksum != calculatedChecksum)
            {
                _logger.LogWarning(
                    "Invalid fiber break response checksum. Received: {Received}, Calculated: {Calculated}",
                    receivedChecksum, calculatedChecksum);
                return;
            }

            if (response[0] != 0xCC || response[1] != 0xAA)
            {
                _logger.LogWarning("Invalid fiber break response header (FLAG2)");
                return;
            }

            if (response[7] != 0x0D)
            {
                _logger.LogWarning(
                    "Invalid fiber break response command (expected 0x0D, got 0x{CMD:X2})",
                    response[7]);
                return;
            }

            if (response.Length < expectedLength)
            {
                _logger.LogWarning(
                    "Fiber break response length mismatch. Expected: {Expected}, Got: {Actual}",
                    expectedLength, response.Length);
                return;
            }

            var timestamp = DateTime.UtcNow;

            for (int channelIndex = 0; channelIndex < numberOfChannels; channelIndex++)
            {
                int dataIndex = channelDataStartIndex + (channelIndex * bytesPerChannel);

                if (dataIndex + bytesPerChannel > response.Length - 1)
                {
                    _logger.LogWarning("Insufficient data for channel {Channel}", channelIndex);
                    break;
                }

                //var systemConfig = await _configurationManager.GetSystemConfigurationAsync();
                //double correctionLength = systemConfig?.CorrectionLength ?? 0;
                //double pointResolution = systemConfig?.DtsPointResolutionMeters ?? DefaultPointResolution;

                byte alarmStatus = response[dataIndex];
                byte positionHigh = response[dataIndex + 1];
                byte positionLow = response[dataIndex + 2];

                int positionRaw = (positionHigh << 8) | positionLow;
                double position = ((positionRaw - 475) * 0.4);

                bool isBroken = alarmStatus == 1;
                int channelId = channelIndex + 1;

                var channelConfig = await _configurationManager.GetChannelConfigurationAsync(channelId);
                double configuredLength = channelConfig?.ChannelLength ?? 0;

                const double pointResolution = 0.4;

                // ignore last point of cable
                double effectiveLength = configuredLength - pointResolution;

                if (isBroken && configuredLength > 0 && position >= effectiveLength)
                {
                    // treat as not broken because it's the cable end
                    isBroken = false;
                }

                //if (isBroken && position < 0)
                //{
                //    isBroken = false;
                //    position = 0;
                //}

                //if (isBroken && configuredLength > 0 && position > configuredLength)
                //{
                //    isBroken = false;
                //    position = configuredLength;
                //}

                bool stateChanged = false;
                if (_previousFiberBreakState.TryGetValue(channelId, out var previousState))
                {
                    stateChanged =
                        previousState.isBroken != isBroken ||
                        Math.Abs(previousState.position - position) > 0.1;
                }
                else
                {
                    stateChanged = true;
                }

                if (stateChanged)
                {
                    // EXISTING EVENT (DO NOT REMOVE)
                    await _eventAggregator.PublishAsync(
                        new FiberBreakDetectedEvent(channelId, isBroken, position, timestamp));

                    // =====================================================
                    // ✅ ADDED: Alarm events so DTSCM updates at startup
                    // =====================================================
                    //if (isBroken)
                    //{
                    //    await _eventAggregator.PublishAsync(
                    //        new AlarmTriggeredEvent(
                    //            channelId,
                    //            0, // ZoneId = 0 for FiberBreak
                    //            AlarmType.FiberBreak,
                    //            position,
                    //            timestamp,
                    //            position));
                    //}
                    //else
                    //{
                    //    await _eventAggregator.PublishAsync(
                    //        new AlarmAutoClearedEvent(
                    //            channelId,
                    //            0,
                    //            AlarmType.FiberBreak,
                    //            timestamp));
                    //}
                    //// =====================================================

                    //if (isBroken)
                    //{
                    //    _logger.LogWarning(
                    //        "Fiber break detected on channel {ChannelId} at position {Position:F1}m",
                    //        channelId, position);
                    //}
                    //else
                    //{
                    //    _logger.LogInformation(
                    //        "Fiber break cleared on channel {ChannelId}",
                    //        channelId);
                    //}

                    _previousFiberBreakState[channelId] = (isBroken, position);
                }
            }

            _logger.LogInformation(
                "Fiber break alarm check completed for {Count} channels",
                numberOfChannels);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing fiber break response");
        }
    }


    public void ResetFiberBreakState()
    {
        _previousFiberBreakState.Clear();
    }

    /// <summary>
    /// Reads DTS module temperature using internal command 0x03 -> 0x03.
    /// </summary>
    private async Task<double> ReadModuleTemperatureAsync()
    {
        await EnsureConnectedAsync();
        var command = DtsCommandBuilder.BuildReadModuleTemperatureCommand();
        var response = await _protocolHandler.SendCommandAndReceiveResponseAsync(command);

        // Response: CC AA 00 00 00 0B FF 03 AA 55 05 FF 03 DATA1 DATA2 SUM
        if (response == null || response.Length < 17)
        {
            _logger.LogWarning("Invalid module temperature response: too short (length={Length})", response?.Length ?? 0);
            return 0;
        }

        // Validate outer header
        if (response[0] != 0xCC || response[1] != 0xAA)
        {
            _logger.LogWarning("Invalid module temperature response header");
            return 0;
        }

        // Validate inner header
        if (response[8] != 0xAA || response[9] != 0x55)
        {
            _logger.LogWarning("Invalid module temperature inner response header");
            return 0;
        }

        // Extract temperature: DATA1 (response[13]) and DATA2 (response[14])
        byte data1 = response[13];
        byte data2 = response[14];

        // Temperature = (DATA1*256+DATA2) / 10
        // Note: Module temperature is a signed number
        short rawTemp = (short)((data1 << 8) | data2);
        double temperature = rawTemp / 10.0;

        _logger.LogDebug("Module temperature read: {Temp}°C (raw: {Raw})", temperature, rawTemp);

        return temperature;
    }

    /// <summary>
    /// Reads DFB laser parameters using internal command 0x03 -> 0xDE.
    /// Returns laser current (mA) and lamp status.
    /// </summary>
    private async Task<(double laserCurrent, bool lampStatus)> ReadDfbLaserParametersAsync()
    {
        await EnsureConnectedAsync();
        var command = DtsCommandBuilder.BuildReadDfbLaserParametersCommand();
        var response = await _protocolHandler.SendCommandAndReceiveResponseAsync(command);

        // Response: CC AA 00 00 00 19 FF 03 AA 55 13 FF DE DFB_Lop(2) DFB_Temp(2) Reserved(6) DFB_Status(2) Pump_Status(2) APD_Status(2) SUM
        if (response == null || response.Length < 31)
        {
            _logger.LogWarning("Invalid DFB laser response: too short (length={Length})", response?.Length ?? 0);
            return (0, false);
        }

        // Validate headers
        if (response[0] != 0xCC || response[1] != 0xAA || response[8] != 0xAA || response[9] != 0x55)
        {
            _logger.LogWarning("Invalid DFB laser response headers");
            return (0, false);
        }

        // Extract DFB_Lop (laser current): bytes 13-14
        ushort laserCurrentRaw = (ushort)((response[13] << 8) | response[14]);
        double laserCurrent = laserCurrentRaw; // Already in mA

        // Extract DFB_Status: bytes 21-22
        ushort dfbStatus = (ushort)((response[21] << 8) | response[22]);
        bool lampStatus = (dfbStatus == 1); // 1 = normal, 0 = abnormal


        _logger.LogDebug("DFB laser current: {Current}mA, Status: {Status}", laserCurrent, lampStatus ? "Normal" : "Abnormal");

        return (laserCurrent, lampStatus);
    }

    /// <summary>
    /// Reads overall DTS parameters using internal command 0x03 -> 0xDD.
    /// Returns pump current (mA) and APD voltage (mV).
    /// </summary>
    private async Task<(double pumpCurrent, double apdValue)> ReadOverallParametersAsync()
    {
        await EnsureConnectedAsync();
        var command = DtsCommandBuilder.BuildReadOverallParametersCommand();
        var response = await _protocolHandler.SendCommandAndReceiveResponseAsync(command);

        // Response: CC AA 00 00 00 23 FF 03 AA 55 1D FF DD P1_Iop_Set(2) P2_Iop_Set(2) Reserved(2) Pump_Mode Apd1_stage Apd2_stage
        //           pulse_width Reserved(3) V1(2) Reserved(3) V2(2) Reserved(3) SUM
        if (response == null || response.Length < 37)
        {
            _logger.LogWarning("Invalid overall parameters response: too short (length={Length})", response?.Length ?? 0);
            return (0, 0);
        }

        // Validate headers
        if (response[0] != 0xCC || response[1] != 0xAA || response[8] != 0xAA || response[9] != 0x55)
        {
            _logger.LogWarning("Invalid overall parameters response headers");
            return (0, 0);
        }

        // Extract P2_Iop_Set (pump current): bytes 15-16
        ushort pumpCurrentRaw = (ushort)((response[15] << 8) | response[16]);
        double pumpCurrent = pumpCurrentRaw / 10.0; // Divide by 10 to get mA

        // Extract V2 (APD voltage): bytes 31-32
        ushort apdVoltageRaw = (ushort)((response[33] << 8) | response[34]);
        double apdValue = apdVoltageRaw; // Already in mV

        _logger.LogDebug("Pump current: {Pump}mA, APD voltage: {Apd}mV", pumpCurrent, apdValue);

        return (pumpCurrent, apdValue);
    }

    /// <summary>
    /// Handles laser control requests from WebSocket or RS232.
    /// </summary>
    /// <summary>
    /// FIXED: Immediately publishes HealthParameterChangedEvent after laser control
    /// to synchronize RS232Manager's _laser flag without waiting for 2-minute health check.
    /// </summary>
    private async Task HandleLaserControlRequestedEvent(LaserControlRequestedEvent ev)
    {
        _logger.LogWarning("###DTS LASER EVENT### type={ControlType} | value={Value} | source={Source}",
            ev.ControlType, ev.Value, ev.Source);

        try
        {
            bool success = false;
            string? errorMessage = null;

            switch (ev.ControlType)
            {
                case Core.Enums.LaserControlType.SetPumpCurrent:
                    _logger.LogWarning("###DTS SET PUMP### calling SetPumpCurrentAsync({Value})", ev.Value);
                    success = await SetPumpCurrentAsync(ev.Value);
                    _logger.LogWarning("###DTS SET PUMP RESULT### success={Success}", success);
                    // FIXED: Immediately sync laser state after pump current change
                    if (success)
                    {
                        await SyncLaserStateAsync("SetPumpCurrent");
                    }
                    break;

                case Core.Enums.LaserControlType.SetPulseWidth:
                    success = await SetPulseWidthAsync((byte)ev.Value);
                    break;

                case Core.Enums.LaserControlType.LaserOn:
                    success = await SetPumpCurrentAsync(DEFAULT_LASER_ON_CURRENT_MA);
                    if (success)
                    {
                        _logger.LogInformation("Laser turned ON (pump current set to {Current}mA)", DEFAULT_LASER_ON_CURRENT_MA);
                        // FIXED: Immediately sync laser state
                        await SyncLaserStateAsync("LaserOn");
                    }
                    break;

                case Core.Enums.LaserControlType.LaserOff:
                    success = await SetPumpCurrentAsync(0);
                    if (success)
                    {
                        _logger.LogInformation("Laser turned OFF (pump current set to 0mA)");
                        // FIXED: Immediately sync laser state
                        await SyncLaserStateAsync("LaserOff");
                    }
                    break;

                default:
                    errorMessage = $"Unknown laser control type: {ev.ControlType}";
                    _logger.LogWarning(errorMessage);
                    break;
            }

            // Publish completion event
            await _eventAggregator.PublishAsync(
                new LaserControlCompletedEvent(ev.ControlType, ev.Value, success, errorMessage));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling laser control request: Type={ControlType}, Value={Value}",
                ev.ControlType, ev.Value);

            await _eventAggregator.PublishAsync(
                new LaserControlCompletedEvent(ev.ControlType, ev.Value, false, ex.Message));
        }
    }

    /// <summary>
    /// ADDED: Immediately reads actual laser current from DTS device and publishes event.
    /// This synchronizes RS232Manager's _laser flag without waiting for periodic health check.
    /// </summary>
    private async Task SyncLaserStateAsync(string source)
    {
        try
        {
            _logger.LogWarning("###DTS SYNC START### source={Source}", source);

            // FIX: Read PumpCurrent (what we control) instead of DFB LaserCurrent (always ~200mA)
            var (pumpCurrent, apdValue) = await ReadOverallParametersAsync();

            _logger.LogWarning("###DTS SYNC READ### PumpCurrent={Current}mA | APD={Apd}mV",
                pumpCurrent, apdValue);

            // Publish event to update RS232Manager's _laser flag immediately
            _logger.LogWarning("###DTS SYNC PUBLISH### publishing PumpCurrent={Current}mA to RS232Manager",
                pumpCurrent);

            await _eventAggregator.PublishAsync(
                new HealthParameterChangedEvent(
                    Core.Enums.HealthParameter.PumpCurrent,
                    pumpCurrent,
                    DateTime.UtcNow));

            _logger.LogWarning("###DTS SYNC DONE### PumpCurrent={Current}mA | source={Source}",
                pumpCurrent, source);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "###DTS SYNC FAILED### source={Source}", source);
            // Not critical - periodic health check will sync eventually
        }
    }

    /// <summary>
    /// Sets the EDFA pump current via TCP command.
    /// </summary>
    /// <param name="currentMa">Pump current in milliamps (mA).</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetPumpCurrentAsync(double currentMa)
    {
        _logger.LogInformation("Setting pump current to {Current}mA", currentMa);

        try
        {
            await EnsureConnectedAsync();
            var command = DtsCommandBuilder.BuildSetPumpCurrentCommand(currentMa);
            var response = await _protocolHandler.SendCommandAndReceiveResponseAsync(command);

            // Validate response
            if (response == null || response.Length < 17)
            {
                _logger.LogWarning("Invalid response for set pump current command (length={Length})", response?.Length ?? 0);
                return false;
            }

            // Validate outer header
            if (response[0] != 0xCC || response[1] != 0xAA)
            {
                _logger.LogWarning("Invalid set pump current response header");
                return false;
            }

            // Validate inner header
            if (response[8] != 0xAA || response[9] != 0x55)
            {
                _logger.LogWarning("Invalid set pump current inner response header");
                return false;
            }

            // Validate command echo
            if (response[12] != 0x1E)
            {
                _logger.LogWarning("Set pump current response command mismatch (expected 0x1E, got 0x{CMD:X2})", response[12]);
                return false;
            }

            _logger.LogInformation("Pump current set successfully to {Current}mA", currentMa);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting pump current to {Current}mA", currentMa);
            return false;
        }
    }

    /// <summary>
    /// Sets the laser pulse width via TCP command.
    /// </summary>
    /// <param name="pulseWidthNs">Pulse width in nanoseconds (ns). Range: 0-200.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetPulseWidthAsync(byte pulseWidthNs)
    {
        _logger.LogInformation("Setting pulse width to {PulseWidth}ns", pulseWidthNs);

        if (pulseWidthNs > 200)
        {
            _logger.LogWarning("Pulse width {PulseWidth}ns exceeds maximum (200ns)", pulseWidthNs);
            return false;
        }

        try
        {
            await EnsureConnectedAsync();
            var command = DtsCommandBuilder.BuildSetPulseWidthCommand(pulseWidthNs);
            var response = await _protocolHandler.SendCommandAndReceiveResponseAsync(command);

            // Validate response
            if (response == null || response.Length < 15)
            {
                _logger.LogWarning("Invalid response for set pulse width command (length={Length})", response?.Length ?? 0);
                return false;
            }

            // Validate outer header
            if (response[0] != 0xCC || response[1] != 0xAA)
            {
                _logger.LogWarning("Invalid set pulse width response header");
                return false;
            }

            // Validate inner header
            if (response[8] != 0xAA || response[9] != 0x55)
            {
                _logger.LogWarning("Invalid set pulse width inner response header");
                return false;
            }

            // Validate command echo
            if (response[12] != 0xC5)
            {
                _logger.LogWarning("Set pulse width response command mismatch (expected 0xC5, got 0x{CMD:X2})", response[12]);
                return false;
            }

            _logger.LogInformation("Pulse width set successfully to {PulseWidth}ns", pulseWidthNs);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting pulse width to {PulseWidth}ns", pulseWidthNs);
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<(bool Success, string? IniPayload, string? ErrorMessage)> ReadConfigurationIniAsync()
    {
        try
        {
            await EnsureConnectedAsync();

            var command = DtsCommandBuilder.BuildReadConfigurationCommand();
            _logger.LogInformation("Sending DTS Read Configuration command (CMD 0x02)...");

            var response = await _protocolHandler.SendCommandAndReceiveResponseAsync(command);

            if (response == null || response.Length < 10)
            {
                return (false, null, "Invalid or empty response from DTS hardware.");
            }

            if (response[0] != 0xCC || response[1] != 0xAA)
            {
                return (false, null, $"Invalid response header: 0x{response[0]:X2} 0x{response[1]:X2} (Expected 0xCC 0xAA)");
            }

            // Extract ASCII INI payload: bytes 8 through response.Length - 2 (excluding checksum at response.Length - 1)
            string iniPayload = Encoding.ASCII.GetString(response, 8, response.Length - 9);

            _logger.LogInformation("Successfully received DTS configuration INI ({Length} characters)", iniPayload.Length);

            // Save to FRMC configuration storage
            await SaveDtsBaselineConfigFileAsync(iniPayload);

            return (true, iniPayload, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read DTS configuration from hardware");
            return (false, null, ex.Message);
        }
    }

    /// <inheritdoc/>
    public async Task<(bool Success, string? ErrorMessage)> WriteConfigurationIniAsync(string iniPayload)
    {
        if (string.IsNullOrWhiteSpace(iniPayload))
        {
            return (false, "INI payload cannot be null or empty.");
        }

        try
        {
            await EnsureConnectedAsync();

            var command = DtsCommandBuilder.BuildWriteConfigurationCommand(iniPayload);
            _logger.LogInformation("Sending DTS Write Configuration command (CMD 0x01, {Length} bytes)...", command.Length);

            var response = await _protocolHandler.SendCommandAndReceiveResponseAsync(command);

            if (response == null || response.Length < 10)
            {
                return (false, "Invalid or incomplete ACK response from DTS hardware.");
            }

            // 1. Header check: CC AA
            if (response[0] != 0xCC || response[1] != 0xAA)
            {
                return (false, $"Invalid ACK header: 0x{response[0]:X2} 0x{response[1]:X2} (Expected 0xCC 0xAA)");
            }

            // 2. Length check: 4-byte Big-Endian declared length == 4
            int declaredLen = (response[2] << 24) | (response[3] << 16) | (response[4] << 8) | response[5];
            if (declaredLen != 4)
            {
                return (false, $"Invalid ACK declared length: {declaredLen} (Expected 4)");
            }

            // 3. Address & Command check: ADR = 0xFF, CMD = 0x01
            if (response[6] != 0xFF || response[7] != 0x01)
            {
                return (false, $"ACK command mismatch: ADR=0x{response[6]:X2}, CMD=0x{response[7]:X2} (Expected ADR=0xFF, CMD=0x01)");
            }

            // 4. Status check: 0x00 = success
            if (response[8] != 0x00)
            {
                return (false, $"DTS hardware returned write failure status: 0x{response[8]:X2}");
            }

            // 5. Checksum check: modulo-256 sum of bytes 0..8
            byte expectedChecksum = 0;
            for (int i = 0; i < 9; i++)
            {
                expectedChecksum += response[i];
            }

            if (response[9] != expectedChecksum)
            {
                return (false, $"ACK checksum mismatch: received 0x{response[9]:X2}, calculated 0x{expectedChecksum:X2}");
            }

            _logger.LogInformation("DTS Write Configuration acknowledged successfully by hardware (Status 0x00, Checksum 0x{Checksum:X2})", response[9]);

            // Persist modified configuration only after verified hardware ACK
            await SaveDtsBaselineConfigFileAsync(iniPayload);

            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write DTS configuration to hardware");
            return (false, ex.Message);
        }
    }

    /// <inheritdoc/>
    public async Task<(bool Success, string? IniPayload, string? ErrorMessage)> ApplyCalibrationAndReadBackAsync(string iniPayload, byte? channelIndex = null)
    {
        _logger.LogInformation("Beginning DTS calibration write-and-readback transaction (ChannelIndex: {ChannelIndex})...", channelIndex);

        var (writeSuccess, writeError) = await WriteConfigurationIniAsync(iniPayload);
        if (!writeSuccess)
        {
            _logger.LogError("DTS calibration write transaction failed: {Error}", writeError);
            return (false, null, writeError);
        }

        // If a channel is specified for calibration calculation, send CMD 0x23 to trigger DTS internal calculation
        if (channelIndex.HasValue)
        {
            try
            {
                var calcCommand = DtsCommandBuilder.BuildTriggerCalibrationCalculationCommand(channelIndex.Value);
                _logger.LogInformation("Sending DTS Trigger Calibration Calculation command (CMD 0x23 for Channel {ChannelIndex})...", channelIndex.Value);
                var calcResponse = await _protocolHandler.SendCommandAndReceiveResponseAsync(calcCommand);

                if (calcResponse != null && calcResponse.Length >= 10 &&
                    calcResponse[0] == 0xCC && calcResponse[1] == 0xAA && calcResponse[7] == 0x23)
                {
                    _logger.LogInformation("DTS Calibration Calculation (CMD 0x23) acknowledged successfully by hardware.");
                }
                else
                {
                    _logger.LogWarning("DTS Calibration Calculation (CMD 0x23) unexpected response or timeout, proceeding to readback.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send CMD 0x23 calibration trigger; proceeding to readback.");
            }
        }

        // Brief delay to allow hardware internal memory synchronization
        await Task.Delay(150);

        _logger.LogInformation("Reading back updated DTS configuration following calibration write (CMD 0x02)...");
        var (readSuccess, readIniPayload, readError) = await ReadConfigurationIniAsync();
        if (!readSuccess || string.IsNullOrEmpty(readIniPayload))
        {
            _logger.LogWarning("Failed to read back updated DTS configuration after write: {Error}", readError);
            // If readback failed but write succeeded, return write success with original payload or error info
            return (false, null, readError ?? "Failed to read back updated configuration from DTS hardware.");
        }

        // If calibration was triggered for a channel, read the updated temperature curve (CMD 0x07)
        if (channelIndex.HasValue)
        {
            try
            {
                _logger.LogInformation("Querying newly calibrated temperature curve from DTS for channel index {ChannelIndex} (CMD 0x07)...", channelIndex.Value);
                await ReadChannelTemperatureAsync(channelIndex.Value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read updated temperature curve for channel {ChannelIndex} following calibration.", channelIndex.Value);
            }
        }

        _logger.LogInformation("DTS calibration write-and-readback transaction completed successfully.");
        return (true, readIniPayload, null);
    }

    private async Task SaveDtsBaselineConfigFileAsync(string iniPayload)
    {
        try
        {
            string configDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config");
            if (!Directory.Exists(configDir))
            {
                Directory.CreateDirectory(configDir);
            }

            // 1. Direct ASCII mirror
            string iniPath = Path.Combine(configDir, "dts_config.ini");
            await File.WriteAllTextAsync(iniPath, iniPayload, Encoding.ASCII);
            _logger.LogInformation("Saved baseline DTS configuration to {Path}", iniPath);

            // 2. Structured dts_configuration.json with actual parsed hardware parameters
            string jsonPath = Path.Combine(configDir, "dts_configuration.json");

            // Extract global parameters
            int channelBitmask = 255;
            var chscanMatch = Regex.Match(iniPayload, @"chscancfg\s*=\s*(\d+)", RegexOptions.IgnoreCase);
            if (chscanMatch.Success && int.TryParse(chscanMatch.Groups[1].Value, out int mask))
            {
                channelBitmask = mask;
            }

            byte pulseWidth = 10;
            var pwMatch = Regex.Match(iniPayload, @"pulsewidth\s*=\s*(\d+)", RegexOptions.IgnoreCase);
            if (pwMatch.Success && byte.TryParse(pwMatch.Groups[1].Value, out byte pw))
            {
                pulseWidth = pw;
            }

            ushort laserCurrent = 56;
            var pcMatch = Regex.Match(iniPayload, @"lasercurrent\s*=\s*(\d+)", RegexOptions.IgnoreCase);
            if (pcMatch.Success && ushort.TryParse(pcMatch.Groups[1].Value, out ushort pc))
            {
                laserCurrent = pc;
            }

            long sysFreq = 250000000;
            var sfMatch = Regex.Match(iniPayload, @"sys_freq\s*=\s*(\d+)", RegexOptions.IgnoreCase);
            if (sfMatch.Success && long.TryParse(sfMatch.Groups[1].Value, out long sf))
            {
                sysFreq = sf;
            }

            // Extract each channel's active state, length, frequency, and calculating time
            var enabledChannels = new List<int>();
            var channelDetails = new List<object>();

            for (int i = 1; i <= 8; i++)
            {
                bool isEnabled = (channelBitmask & (1 << (i - 1))) != 0;
                if (isEnabled) enabledChannels.Add(i);

                double cableLength = 6000;
                int pulseFreq = 14000;
                long avgnum = 84000;
                int calcSeconds = 6;

                string sectionHeader = $"[ch{i}_basic]";
                int sectionStart = iniPayload.IndexOf(sectionHeader, StringComparison.OrdinalIgnoreCase);
                if (sectionStart >= 0)
                {
                    int nextSection = iniPayload.IndexOf("[", sectionStart + sectionHeader.Length);
                    string sectionContent = (nextSection > sectionStart)
                        ? iniPayload.Substring(sectionStart, nextSection - sectionStart)
                        : iniPayload.Substring(sectionStart);

                    var lenM = Regex.Match(sectionContent, @"chlen\s*=\s*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
                    if (lenM.Success && double.TryParse(lenM.Groups[1].Value, out double cl))
                        cableLength = cl;

                    var freqM = Regex.Match(sectionContent, @"pulsefreq\s*=\s*(\d+)", RegexOptions.IgnoreCase);
                    if (freqM.Success && int.TryParse(freqM.Groups[1].Value, out int pf))
                        pulseFreq = pf;

                    var avgM = Regex.Match(sectionContent, @"avgnum\s*=\s*(\d+)", RegexOptions.IgnoreCase);
                    if (avgM.Success && long.TryParse(avgM.Groups[1].Value, out long an))
                    {
                        avgnum = an;
                        calcSeconds = (pulseFreq > 0) ? (int)Math.Round((double)avgnum / pulseFreq) : 6;
                    }
                }

                channelDetails.Add(new
                {
                    ChannelId = i,
                    IsEnabled = isEnabled,
                    CableLengthMeters = cableLength,
                    PulseFrequencyHz = pulseFreq,
                    AccumulationCount = avgnum,
                    CalculatingTimeSeconds = calcSeconds
                });
            }

            var jsonObject = new
            {
                LastUpdated = DateTime.UtcNow.ToString("o"),
                DtsIpAddress = "192.168.24.11",
                DtsPort = 3000,
                ChannelBitmask = channelBitmask,
                EnabledChannelIds = enabledChannels,
                PulseWidthNs = pulseWidth,
                LaserCurrentMa = laserCurrent,
                SystemSamplingFrequencyHz = sysFreq,
                Channels = channelDetails,
                RawIniPayload = iniPayload
            };

            var json = JsonSerializer.Serialize(jsonObject, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(jsonPath, json);
            _logger.LogInformation("Updated DTS configuration JSON with {Count} enabled channels in {Path}", enabledChannels.Count, jsonPath);

            // Also mirror to current working directory Config/ if different (e.g. source folder when running from IDE)
            try
            {
                string workingConfigDir = Path.Combine(Directory.GetCurrentDirectory(), "Config");
                if (Directory.Exists(workingConfigDir) && !string.Equals(workingConfigDir, configDir, StringComparison.OrdinalIgnoreCase))
                {
                    await File.WriteAllTextAsync(Path.Combine(workingConfigDir, "dts_config.ini"), iniPayload, Encoding.ASCII);
                    await File.WriteAllTextAsync(Path.Combine(workingConfigDir, "dts_configuration.json"), json);
                    _logger.LogInformation("Mirrored DTS configuration to working directory {Path}", workingConfigDir);
                }
            }
            catch
            {
                // Non-critical mirror
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save baseline DTS configuration file");
        }
    }

    /// <summary>
    /// Disposes resources including timers, semaphores, and buffer pool.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Log buffer pool statistics before disposal
        var (rentals, returns, active) = _bufferPool.GetStatistics();
        _logger.LogInformation(
            "Buffer pool stats: Rentals={Rentals}, Returns={Returns}, Active={Active}, EstimatedSaved={SavedMB:F1}MB",
            rentals, returns, active, _bufferPool.EstimateMemorySaved() / (1024.0 * 1024.0));

        // Dispose buffer pool (returns all arrays to ArrayPool)
        _bufferPool.Dispose();

        // Dispose timers
        _fiberBreakCheckTimer?.Dispose();
        _healthCheckTimer?.Dispose();

        // Dispose semaphores
        _fiberBreakCheckSemaphore?.Dispose();
        _healthCheckSemaphore?.Dispose();

        _logger.LogInformation("DtsManager disposed");
    }
}
