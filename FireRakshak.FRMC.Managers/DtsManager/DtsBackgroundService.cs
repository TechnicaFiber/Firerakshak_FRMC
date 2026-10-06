using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FireRakshak.FRMC.Managers.SystemConfigurationManager;
using FireRakshak.FRMC.Core.EventAggregator;
using FireRakshak.FRMC.Core.Events;
using FireRakshak.FRMC.Core.Models;
using FireRakshak.FRMC.Core.Enums;

namespace FireRakshak.FRMC.Managers.DtsManager
{
    public class DtsBackgroundService : BackgroundService
    {
        private readonly IDtsManager _dtsManager;
        private readonly DtsDataPipeline? _dataPipeline;
        private readonly IConfigurationManager _configurationManager;
        private readonly IEventAggregator _eventAggregator;
        private readonly ILogger<DtsBackgroundService> _logger;
        private readonly DtsOptions _options;
        private readonly bool _useParallelProcessing;
        private TimeSpan _interval;

        // Constants for delay calculation based on channel length
        private const double BASE_DELAY_MS = 50;  // Minimum delay for protocol overhead
        private const double DELAY_PER_KM_MS = 20; // Additional delay per km of fiber (accounts for data transfer)

        public DtsBackgroundService(
            IDtsManager dtsManager,
            DtsDataPipeline? dataPipeline,
            IConfigurationManager configurationManager,
            IEventAggregator eventAggregator,
            ILogger<DtsBackgroundService> logger,
            IOptions<DtsOptions> options)
        {
            _dtsManager = dtsManager;
            _dataPipeline = dataPipeline;
            _configurationManager = configurationManager;
            _eventAggregator = eventAggregator;
            _logger = logger;
            _options = options.Value;
            _useParallelProcessing = _options.EnableParallelProcessing && _dataPipeline != null;

            // Default interval (will be updated in StartAsync)
            _interval = TimeSpan.FromSeconds(6);

            // Subscribe to configuration changes
            _eventAggregator.Subscribe<ConfigurationChangedEvent>(HandleConfigurationChanged);

            _logger.LogInformation(
                "DtsBackgroundService initialized - Parallel processing: {Enabled}",
                _useParallelProcessing);
        }

        private Task HandleConfigurationChanged(ConfigurationChangedEvent ev)
        {
            if (ev.ConfigType == ConfigurationType.System && ev.Changes.ContainsKey("DtsPollingInterval"))
            {
                var newInterval = (int)ev.Changes["DtsPollingInterval"];
                _interval = TimeSpan.FromMilliseconds(newInterval);
                _logger.LogInformation("DTS polling interval updated to {Interval}ms", newInterval);
            }
            return Task.CompletedTask;
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            // Initialize interval from system configuration
            var sysConfig = await _configurationManager.GetSystemConfigurationAsync();
            _interval = TimeSpan.FromMilliseconds(sysConfig.DtsPollingInterval);
            _logger.LogInformation("DTS Background Service starting with polling interval {Interval}ms", sysConfig.DtsPollingInterval);

            try
            {
                _logger.LogInformation("Applying DTS configuration from JSON at startup");
                //await _dtsManager.ConfigureFromJsonAsync(); // Will be used in future version of Software
                _logger.LogInformation("DTS startup configuration completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DTS startup configuration failed");
            }

            await base.StartAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("DTS Background Service running (parallel processing: {UseParallel}).",
                _useParallelProcessing);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Connect only once or if the connection is lost
                    if (!_dtsManager.IsConnected)
                    {
                        _logger.LogInformation("Connecting to DTS...");
                        await _dtsManager.ConnectAsync();
                        _logger.LogInformation("Connected to DTS successfully.");
                    }

                    // Get enabled channels from configuration manager
                    var channels = _configurationManager.GetChannels()
                        .Where(c => c.Configuration.IsEnabled)
                        .OrderBy(c => c.Configuration.ChannelId)
                        .ToList();

                    if (channels.Count == 0)
                    {
                        _logger.LogWarning("No enabled channels found in configuration. Waiting...");
                        await Task.Delay(_interval, stoppingToken);
                        continue;
                    }

                    if (_useParallelProcessing)
                    {
                        // PARALLEL MODE: Sequential TCP reads, parallel processing via pipeline
                        await ExecuteParallelModeAsync(channels, stoppingToken);
                    }
                    else
                    {
                        // SEQUENTIAL MODE: Original behavior (read + process sequentially)
                        await ExecuteSequentialModeAsync(channels, stoppingToken);
                    }
                }
                catch (IOException ioEx)
                {
                    _logger.LogWarning(ioEx, "DTS connection lost. Will retry on next cycle...");
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error while reading DTS data.");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }
            }
        }

        /// <summary>
        /// Executes parallel processing mode: TCP reads are sequential, processing is parallel via pipeline.
        /// </summary>
        private async Task ExecuteParallelModeAsync(List<Channel> channels, CancellationToken stoppingToken)
        {
            var cycleStart = DateTime.UtcNow;

            _logger.LogDebug(
                "Parallel mode: Reading {ChannelCount} channels sequentially, processing in parallel",
                channels.Count);

            foreach (var channel in channels)
            {
                if (stoppingToken.IsCancellationRequested)
                    break;

                try
                {
                    // Convert from 1-based (application) to 0-based (DTS hardware)
                    byte dtsChannelId = (byte)(channel.Configuration.ChannelId - 1);
                    int frmcChannelId = channel.Configuration.ChannelId;

                    // Read raw data (sequential TCP - hardware constraint)
                    var rawData = await _dtsManager.ReadChannelRawDataAsync(dtsChannelId);

                    // Enqueue for parallel processing
                    if (rawData != null && _dataPipeline != null)
                    {
                        await _dataPipeline.EnqueueAsync(frmcChannelId, rawData, DateTime.UtcNow);
                    }
                }
                catch (Exception channelEx)
                {
                    _logger.LogError(channelEx, "Error reading channel {ChannelId}",
                        channel.Configuration.ChannelId);
                    // Continue with next channel even if one fails
                }

                // NOTE: Inter-read delay removed in parallel mode.
                // The CommandDelayMs inside DtsProtocolHandler already provides
                // sufficient hardware recovery time between sequential TCP reads.
                // Previously this added a redundant 100ms delay per channel,
                // wasting 800ms per 8-channel cycle.
            }

            // Calculate remaining time to respect the polling interval
            // This allows pipeline workers to process while we wait
            var elapsed = DateTime.UtcNow - cycleStart;
            var remainingTime = _interval - elapsed;

            if (remainingTime > TimeSpan.Zero)
            {
                _logger.LogDebug(
                    "Parallel mode: Reads completed in {Elapsed:F0}ms, waiting {Remaining:F0}ms for interval",
                    elapsed.TotalMilliseconds, remainingTime.TotalMilliseconds);
                await Task.Delay(remainingTime, stoppingToken);
            }
            else
            {
                _logger.LogDebug(
                    "Parallel mode: Reads took {Elapsed:F0}ms (exceeded interval of {Interval:F0}ms)",
                    elapsed.TotalMilliseconds, _interval.TotalMilliseconds);
            }
        }

        /// <summary>
        /// Executes sequential processing mode: Original behavior with per-channel delays.
        /// </summary>
        private async Task ExecuteSequentialModeAsync(List<Channel> channels, CancellationToken stoppingToken)
        {
            // Calculate per-channel delays based on channel length
            var channelDelays = CalculateOptimalDelays(channels, _interval);

            var totalDelay = channelDelays.Values.Sum(d => d.TotalMilliseconds);
            _logger.LogDebug(
                "Sequential mode: Scanning {ChannelCount} channels (total cycle: {TotalCycle:F0}ms)",
                channels.Count, totalDelay);

            // Read temperature for each enabled channel with optimized delay
            foreach (var channel in channels)
            {
                if (stoppingToken.IsCancellationRequested)
                    break;

                try
                {
                    // Convert from 1-based (application) to 0-based (DTS hardware)
                    byte dtsChannelId = (byte)(channel.Configuration.ChannelId - 1);

                    await _dtsManager.ReadChannelTemperatureAsync(dtsChannelId);
                }
                catch (Exception channelEx)
                {
                    _logger.LogError(channelEx, "Error reading channel {ChannelId}",
                        channel.Configuration.ChannelId);
                    // Continue with next channel even if one fails
                }

                // Delay after each channel based on its length
                var delay = channelDelays[channel.Configuration.ChannelId];
                await Task.Delay(delay, stoppingToken);
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("DTS Background Service stopping.");

            // Stop the pipeline gracefully first (allow in-flight processing to complete)
            if (_dataPipeline != null && _useParallelProcessing)
            {
                try
                {
                    _logger.LogInformation("Stopping DTS data pipeline...");
                    await _dataPipeline.StopAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error while stopping DTS data pipeline.");
                }
            }

            try
            {
                await _dtsManager.DisconnectAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error while disconnecting DTS.");
            }

            await base.StopAsync(cancellationToken);
        }

        /// <summary>
        /// Calculates optimal delay for each channel based on its configured length.
        /// Longer channels get proportionally more delay to account for:
        /// - More data points to transfer (at 0.4m resolution)
        /// - Larger response parsing time
        ///
        /// Formula: delay = BASE_DELAY_MS + (channelLengthKm * DELAY_PER_KM_MS)
        /// Then scaled to fit within the total polling interval.
        /// </summary>
        private Dictionary<int, TimeSpan> CalculateOptimalDelays(
            List<Channel> channels,
            TimeSpan totalInterval)
        {
            var delays = new Dictionary<int, TimeSpan>();

            if (channels.Count == 0)
                return delays;

            // Calculate raw delays based on channel length
            var rawDelays = new Dictionary<int, double>();
            double totalRawDelay = 0;

            foreach (var channel in channels)
            {
                // Get channel length in km (ChannelLength is in meters)
                double lengthKm = channel.Configuration.ChannelLength / 1000.0;

                // Calculate delay: base + proportional to length
                double rawDelay = BASE_DELAY_MS + (lengthKm * DELAY_PER_KM_MS);
                rawDelays[channel.Configuration.ChannelId] = rawDelay;
                totalRawDelay += rawDelay;
            }

            // Scale delays to fit within the total polling interval
            double scaleFactor = totalInterval.TotalMilliseconds / totalRawDelay;

            foreach (var channel in channels)
            {
                double scaledDelay = rawDelays[channel.Configuration.ChannelId] * scaleFactor;

                // Ensure minimum delay of 10ms to prevent CPU spinning
                scaledDelay = Math.Max(scaledDelay, 10);

                delays[channel.Configuration.ChannelId] = TimeSpan.FromMilliseconds(scaledDelay);

                _logger.LogTrace(
                    "Channel {ChannelId}: length={Length}m, delay={Delay:F0}ms",
                    channel.Configuration.ChannelId,
                    channel.Configuration.ChannelLength,
                    scaledDelay);
            }

            return delays;
        }
    }
}
