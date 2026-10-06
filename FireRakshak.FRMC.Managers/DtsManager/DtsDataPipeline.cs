using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FireRakshak.FRMC.Core.EventAggregator;
using FireRakshak.FRMC.Core.Events;
using FireRakshak.FRMC.Core.Models;
using FireRakshak.FRMC.Managers.SystemConfigurationManager;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Alias to resolve ambiguity between System.Threading.Channels.Channel and FireRakshak.FRMC.Core.Models.Channel
using ChannelType = System.Threading.Channels.Channel;

namespace FireRakshak.FRMC.Managers.DtsManager;

/// <summary>
/// Producer-consumer pipeline for parallel processing of DTS temperature data.
/// Keeps TCP reads sequential (hardware constraint) while parallelizing data processing.
/// </summary>
public class DtsDataPipeline : IDisposable
{
    private readonly Channel<ChannelTemperatureData> _dataChannel;
    private readonly IEventAggregator _eventAggregator;
    private readonly IConfigurationManager _configurationManager;
    private readonly ILogger<DtsDataPipeline> _logger;
    private readonly DtsOptions _options;
    private readonly Task[] _workerTasks;
    private readonly CancellationTokenSource _cts;
    private bool _disposed;

    // Constants for temperature parsing (same as DtsManager)
    private const int BaseDiscardedCount = 475;
    private const double DefaultPointResolution = 0.4; // Default if not configured

    /// <summary>
    /// Represents raw temperature data from a DTS channel awaiting processing.
    /// </summary>
    public record ChannelTemperatureData(int ChannelId, byte[] RawResponse, DateTime Timestamp);

    public DtsDataPipeline(
        IEventAggregator eventAggregator,
        IConfigurationManager configurationManager,
        ILogger<DtsDataPipeline> logger,
        IOptions<DtsOptions> options)
    {
        _eventAggregator = eventAggregator;
        _configurationManager = configurationManager;
        _logger = logger;
        _options = options.Value;
        _cts = new CancellationTokenSource();

        // Create bounded channel to prevent memory overflow
        // Capacity of 16 allows buffering for all channels without excessive memory use
        _dataChannel = ChannelType.CreateBounded<ChannelTemperatureData>(
            new BoundedChannelOptions(16)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = true
            });

        // Start worker tasks for parallel processing
        int workerCount = Math.Max(1, _options.PipelineWorkerCount);
        _workerTasks = new Task[workerCount];

        for (int i = 0; i < workerCount; i++)
        {
            int workerId = i;
            _workerTasks[i] = Task.Run(() => ProcessDataAsync(workerId, _cts.Token));
        }

        _logger.LogInformation(
            "DtsDataPipeline initialized with {WorkerCount} workers, channel capacity: 16",
            workerCount);
    }

    /// <summary>
    /// Enqueues raw temperature data for asynchronous processing.
    /// Called by producer (DtsBackgroundService) after each TCP read.
    /// </summary>
    public async ValueTask EnqueueAsync(int channelId, byte[]? rawResponse, DateTime timestamp)
    {
        if (rawResponse == null || rawResponse.Length == 0)
        {
            _logger.LogWarning("Skipping empty response for channel {ChannelId}", channelId);
            return;
        }

        var data = new ChannelTemperatureData(channelId, rawResponse, timestamp);

        try
        {
            await _dataChannel.Writer.WriteAsync(data, _cts.Token);
            _logger.LogDebug("Enqueued data for channel {ChannelId}, {Length} bytes", channelId, rawResponse.Length);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Pipeline shutdown - skipping enqueue for channel {ChannelId}", channelId);
        }
        catch (ChannelClosedException)
        {
            _logger.LogDebug("Pipeline closed - skipping enqueue for channel {ChannelId}", channelId);
        }
    }

    /// <summary>
    /// Worker method that processes queued temperature data in parallel.
    /// </summary>
    private async Task ProcessDataAsync(int workerId, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Pipeline worker {WorkerId} started", workerId);

        try
        {
            await foreach (var data in _dataChannel.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await ProcessTemperatureDataAsync(data);
                }
                catch (Exception ex)
                {
                    // Log but don't crash the worker - continue processing
                    _logger.LogError(ex,
                        "Worker {WorkerId}: Error processing channel {ChannelId} data",
                        workerId, data.ChannelId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Pipeline worker {WorkerId} stopped (cancellation)", workerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipeline worker {WorkerId} terminated unexpectedly", workerId);
        }

        _logger.LogDebug("Pipeline worker {WorkerId} exited", workerId);
    }

    /// <summary>
    /// Processes raw temperature response: parse, filter, and publish event.
    /// </summary>
    private async Task ProcessTemperatureDataAsync(ChannelTemperatureData data)
    {
        var temperatureData = await ParseTemperatureResponseAsync(data.RawResponse, data.ChannelId);

        if (temperatureData.Length == 0)
        {
            _logger.LogWarning("No valid temperature data parsed for channel {ChannelId}", data.ChannelId);
            return;
        }

        // Filter temperature data based on configured channel length
        var channelConfig = await _configurationManager.GetChannelConfigurationAsync(data.ChannelId);
        if (channelConfig != null && channelConfig.ChannelLength > 0)
        {
            double configuredLength = channelConfig.ChannelLength;
            int originalCount = temperatureData.Length;

            temperatureData = FilterByChannelLength(temperatureData, configuredLength);

            _logger.LogDebug(
                "Channel {ChannelId}: Filtered from {Original} to {Filtered} points (length: {Length}m)",
                data.ChannelId, originalCount, temperatureData.Length, configuredLength);
        }

        // Publish temperature data event
        await _eventAggregator.PublishAsync(
            new TemperatureDataReceivedEvent(data.ChannelId, temperatureData, data.Timestamp));

        _logger.LogDebug(
            "Published temperature data for channel {ChannelId}: {Count} points",
            data.ChannelId, temperatureData.Length);
    }

    /// <summary>
    /// Parses DTS temperature response bytes into TemperatureDataPoint array.
    /// </summary>
    private async Task<TemperatureDataPoint[]> ParseTemperatureResponseAsync(byte[] response, int channelId)
    {
        try
        {
            const int minResponseLength = 14;

            if (response == null || response.Length < minResponseLength)
            {
                _logger.LogWarning("Invalid temperature response: too short for channel {ChannelId}", channelId);
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
                _logger.LogWarning(
                    "Channel {ChannelId}: Invalid checksum. Received: {Received}, Calculated: {Calculated}",
                    channelId, receivedChecksum, calculatedChecksum);
                return Array.Empty<TemperatureDataPoint>();
            }

            // FLAG2 validation
            if (response[0] != 0xCC || response[1] != 0xAA)
            {
                _logger.LogWarning("Channel {ChannelId}: Invalid response header (FLAG2)", channelId);
                return Array.Empty<TemperatureDataPoint>();
            }

            // Get correction length and point resolution from system config
            var systemConfig = await _configurationManager.GetSystemConfigurationAsync();
            double correctionLength = systemConfig?.CorrectionLength ?? 0;
            double pointResolution = systemConfig?.DtsPointResolutionMeters ?? DefaultPointResolution;

            int totalTempCount = (response.Length - minResponseLength) / 2;
            const int discardCount = 475;

            if (totalTempCount <= discardCount)
            {
                _logger.LogWarning(
                    "Channel {ChannelId}: Not enough samples ({Count}) to discard {Discard}",
                    channelId, totalTempCount, discardCount);
                return Array.Empty<TemperatureDataPoint>();
            }

            int validCount = totalTempCount - discardCount;
            var dataPoints = new TemperatureDataPoint[validCount];

            // Use configurable point resolution from system config
            double distanceResolution = pointResolution;

            for (int i = 0; i < validCount; i++)
            {
                int sampleIndex = discardCount + i;
                int dataIndex = 13 + (sampleIndex * 2);

                if (dataIndex + 1 >= response.Length - 1)
                {
                    _logger.LogWarning(
                        "Channel {ChannelId}: Unexpected response length at sample {Sample}",
                        channelId, sampleIndex);
                    break;
                }

                // Temperature is 16-bit signed big-endian
                short rawTemp = (short)((response[dataIndex] << 8) | response[dataIndex + 1]);
                double temperature = rawTemp / 10.0;

                // Position with correction length applied
                double position = (i * distanceResolution) - correctionLength;

                dataPoints[i] = new TemperatureDataPoint(temperature, position);
            }

            return dataPoints;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing temperature response for channel {ChannelId}", channelId);
            return Array.Empty<TemperatureDataPoint>();
        }
    }

    /// <summary>
    /// Filters temperature data points to only include those within the configured channel length.
    /// </summary>
    private static TemperatureDataPoint[] FilterByChannelLength(TemperatureDataPoint[] data, double maxLength)
    {
        int count = 0;
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i].Position <= maxLength)
                count++;
        }

        if (count == data.Length)
            return data;

        var filtered = new TemperatureDataPoint[count];
        int idx = 0;
        for (int i = 0; i < data.Length && idx < count; i++)
        {
            if (data[i].Position <= maxLength)
                filtered[idx++] = data[i];
        }

        return filtered;
    }

    /// <summary>
    /// Gracefully stops the pipeline, allowing in-flight processing to complete.
    /// </summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        _logger.LogInformation("Stopping DtsDataPipeline...");

        // Signal writer to complete
        _dataChannel.Writer.TryComplete();

        // Wait for workers to drain the channel
        using var timeoutCts = new CancellationTokenSource(timeout);
        try
        {
            await Task.WhenAll(_workerTasks).WaitAsync(timeoutCts.Token);
            _logger.LogInformation("DtsDataPipeline stopped gracefully");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("DtsDataPipeline stop timed out - cancelling workers");
            _cts.Cancel();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _cts.Cancel();
        _dataChannel.Writer.TryComplete();

        try
        {
            // Wait for workers to complete with timeout
            if (!Task.WhenAll(_workerTasks).Wait(TimeSpan.FromSeconds(2)))
            {
                _logger.LogWarning("Pipeline workers did not complete within timeout during dispose");
            }
        }
        catch (AggregateException)
        {
            // Workers may throw on cancellation - expected behavior
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error waiting for pipeline workers during dispose");
        }

        _cts.Dispose();
        _logger.LogInformation("DtsDataPipeline disposed");
    }
}
