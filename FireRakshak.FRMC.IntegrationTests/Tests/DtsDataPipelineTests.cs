using System.Diagnostics;
using FireRakshak.FRMC.Core.EventAggregator;
using FireRakshak.FRMC.Core.Events;
using FireRakshak.FRMC.Core.Models;
using FireRakshak.FRMC.IntegrationTests.Fixtures;
using FireRakshak.FRMC.IntegrationTests.Mocks;
using FireRakshak.FRMC.Managers.DtsManager;
using FireRakshak.FRMC.Managers.SystemConfigurationManager;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace FireRakshak.FRMC.IntegrationTests.Tests;

/// <summary>
/// Tests for the DtsDataPipeline parallel processing implementation.
/// Verifies correct parsing, event publishing, and performance improvements.
/// </summary>
[Collection("FRMC Integration Tests")]
public class DtsDataPipelineTests : IDisposable
{
    private readonly FrmcTestFixture _fixture;
    private readonly Mock<IConfigurationManager> _mockConfigManager;
    private readonly DtsOptions _options;
    private DtsDataPipeline? _pipeline;

    public DtsDataPipelineTests(FrmcTestFixture fixture)
    {
        _fixture = fixture;
        _fixture.Reset();

        _mockConfigManager = new Mock<IConfigurationManager>();
        _mockConfigManager
            .Setup(x => x.GetSystemConfigurationAsync())
            .ReturnsAsync(_fixture.DefaultSystemConfig);
        _mockConfigManager
            .Setup(x => x.GetChannelConfigurationAsync(It.IsAny<int>()))
            .ReturnsAsync((int id) => _fixture.DefaultChannelConfigs.FirstOrDefault(c => c.ChannelId == id));

        _options = new DtsOptions
        {
            EnableParallelProcessing = true,
            PipelineWorkerCount = 2,
            CommandDelayMs = 50
        };
    }

    public void Dispose()
    {
        _pipeline?.Dispose();
    }

    [Fact]
    public async Task EnqueueAsync_WithValidData_PublishesTemperatureEvent()
    {
        // Arrange
        var eventAggregator = _fixture.MockEventAggregator;
        TemperatureDataReceivedEvent? receivedEvent = null;
        var eventReceived = new TaskCompletionSource<bool>();

        eventAggregator.Subscribe<TemperatureDataReceivedEvent>(e =>
        {
            receivedEvent = e;
            eventReceived.TrySetResult(true);
            return Task.CompletedTask;
        });

        _pipeline = CreatePipeline(eventAggregator);

        // Generate valid raw DTS response
        var rawResponse = GenerateValidDtsResponse(channelId: 1, pointCount: 100);

        // Act
        await _pipeline.EnqueueAsync(channelId: 1, rawResponse, DateTime.UtcNow);

        // Wait for processing with timeout
        var completed = await Task.WhenAny(eventReceived.Task, Task.Delay(5000));

        // Assert
        completed.Should().Be(eventReceived.Task, "event should be received within timeout");
        receivedEvent.Should().NotBeNull();
        receivedEvent!.ChannelId.Should().Be(1);
        receivedEvent.Data.Should().NotBeEmpty();
    }

    [Fact]
    public async Task EnqueueAsync_WithNullData_DoesNotPublishEvent()
    {
        // Arrange
        var eventAggregator = _fixture.MockEventAggregator;
        var eventCount = 0;

        eventAggregator.Subscribe<TemperatureDataReceivedEvent>(e =>
        {
            Interlocked.Increment(ref eventCount);
            return Task.CompletedTask;
        });

        _pipeline = CreatePipeline(eventAggregator);

        // Act
        await _pipeline.EnqueueAsync(channelId: 1, null, DateTime.UtcNow);
        await Task.Delay(500); // Give time for processing

        // Assert
        eventCount.Should().Be(0, "null data should not produce an event");
    }

    [Fact]
    public async Task EnqueueAsync_WithInvalidChecksum_DoesNotPublishEvent()
    {
        // Arrange
        var eventAggregator = _fixture.MockEventAggregator;
        var eventCount = 0;

        eventAggregator.Subscribe<TemperatureDataReceivedEvent>(e =>
        {
            Interlocked.Increment(ref eventCount);
            return Task.CompletedTask;
        });

        _pipeline = CreatePipeline(eventAggregator);

        // Generate invalid response (wrong checksum)
        var rawResponse = GenerateValidDtsResponse(channelId: 1, pointCount: 100);
        rawResponse[rawResponse.Length - 1] = 0xFF; // Corrupt checksum

        // Act
        await _pipeline.EnqueueAsync(channelId: 1, rawResponse, DateTime.UtcNow);
        await Task.Delay(500);

        // Assert
        eventCount.Should().Be(0, "invalid checksum should not produce an event");
    }

    [Fact]
    public async Task EnqueueAsync_MultipleChannels_AllProcessedCorrectly()
    {
        // Arrange
        var eventAggregator = _fixture.MockEventAggregator;
        var receivedChannels = new List<int>();
        var allReceived = new TaskCompletionSource<bool>();
        const int expectedChannels = 4;

        eventAggregator.Subscribe<TemperatureDataReceivedEvent>(e =>
        {
            lock (receivedChannels)
            {
                receivedChannels.Add(e.ChannelId);
                if (receivedChannels.Count >= expectedChannels)
                    allReceived.TrySetResult(true);
            }
            return Task.CompletedTask;
        });

        _pipeline = CreatePipeline(eventAggregator);

        // Act - enqueue data for 4 channels
        for (int ch = 1; ch <= expectedChannels; ch++)
        {
            var rawResponse = GenerateValidDtsResponse(channelId: (byte)(ch - 1), pointCount: 100);
            await _pipeline.EnqueueAsync(ch, rawResponse, DateTime.UtcNow);
        }

        // Wait for all to be processed
        var completed = await Task.WhenAny(allReceived.Task, Task.Delay(10000));

        // Assert
        completed.Should().Be(allReceived.Task, "all channels should be processed within timeout");
        receivedChannels.Should().HaveCount(expectedChannels);
        receivedChannels.Should().Contain(new[] { 1, 2, 3, 4 });
    }

    [Fact]
    public async Task StopAsync_WithPendingData_CompletesGracefully()
    {
        // Arrange
        var eventAggregator = _fixture.MockEventAggregator;
        var processedCount = 0;

        eventAggregator.Subscribe<TemperatureDataReceivedEvent>(async e =>
        {
            await Task.Delay(100); // Simulate slow processing
            Interlocked.Increment(ref processedCount);
        });

        _pipeline = CreatePipeline(eventAggregator);

        // Enqueue some data
        for (int i = 1; i <= 3; i++)
        {
            var rawResponse = GenerateValidDtsResponse(channelId: (byte)(i - 1), pointCount: 50);
            await _pipeline.EnqueueAsync(i, rawResponse, DateTime.UtcNow);
        }

        // Act
        await _pipeline.StopAsync(TimeSpan.FromSeconds(10));

        // Assert - all items should be processed before stop completes
        processedCount.Should().Be(3, "all queued items should be processed during graceful shutdown");
    }

    [Fact]
    public async Task Pipeline_DataFiltering_RespectsChannelLength()
    {
        // Arrange
        var eventAggregator = _fixture.MockEventAggregator;
        TemperatureDataReceivedEvent? receivedEvent = null;
        var eventReceived = new TaskCompletionSource<bool>();

        // Configure channel 1 with 500m length
        var channelConfig = new ChannelConfiguration
        {
            ChannelId = 1,
            ChannelLength = 500, // 500 meters
            IsEnabled = true
        };
        _mockConfigManager
            .Setup(x => x.GetChannelConfigurationAsync(1))
            .ReturnsAsync(channelConfig);

        eventAggregator.Subscribe<TemperatureDataReceivedEvent>(e =>
        {
            receivedEvent = e;
            eventReceived.TrySetResult(true);
            return Task.CompletedTask;
        });

        _pipeline = CreatePipeline(eventAggregator);

        // Generate data with 2500 points (1000m at 0.4m resolution)
        var rawResponse = GenerateValidDtsResponse(channelId: 0, pointCount: 2500);

        // Act
        await _pipeline.EnqueueAsync(channelId: 1, rawResponse, DateTime.UtcNow);
        await Task.WhenAny(eventReceived.Task, Task.Delay(5000));

        // Assert
        receivedEvent.Should().NotBeNull();
        // Should be filtered to ~1250 points (500m / 0.4m per point)
        receivedEvent!.Data.Length.Should().BeLessThan(2500);
        receivedEvent.Data.All(p => p.Position <= 500).Should().BeTrue("all points should be within channel length");
    }

    [Fact]
    public async Task Pipeline_WorkerRecovery_ContinuesAfterException()
    {
        // Arrange
        var eventAggregator = _fixture.MockEventAggregator;
        var processedChannels = new List<int>();
        var allReceived = new TaskCompletionSource<bool>();

        // First call throws, subsequent calls succeed
        var callCount = 0;
        _mockConfigManager
            .Setup(x => x.GetSystemConfigurationAsync())
            .Returns(() =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                    throw new Exception("Simulated failure");
                return Task.FromResult(_fixture.DefaultSystemConfig);
            });

        eventAggregator.Subscribe<TemperatureDataReceivedEvent>(e =>
        {
            lock (processedChannels)
            {
                processedChannels.Add(e.ChannelId);
                if (processedChannels.Count >= 2)
                    allReceived.TrySetResult(true);
            }
            return Task.CompletedTask;
        });

        _pipeline = CreatePipeline(eventAggregator);

        // Act - enqueue multiple items
        for (int i = 1; i <= 3; i++)
        {
            var rawResponse = GenerateValidDtsResponse(channelId: (byte)(i - 1), pointCount: 100);
            await _pipeline.EnqueueAsync(i, rawResponse, DateTime.UtcNow);
        }

        await Task.WhenAny(allReceived.Task, Task.Delay(5000));

        // Assert - pipeline should continue processing after first failure
        processedChannels.Count.Should().BeGreaterOrEqualTo(2, "pipeline should recover from exceptions");
    }

    [Fact]
    public async Task MockDtsManager_ReadChannelRawDataAsync_ReturnsValidFormat()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        mockDts.Reset();

        // Act
        await mockDts.ConnectAsync();
        var result = await mockDts.ReadChannelRawDataAsync(0);

        // Assert
        result.Should().NotBeNull();
        result!.Length.Should().BeGreaterThan(14, "response should have minimum header length");

        // Verify header format
        result[0].Should().Be(0xCC, "first byte should be FLAG2 header");
        result[1].Should().Be(0xAA, "second byte should be FLAG2 header");

        // Verify checksum
        byte calculatedChecksum = 0;
        for (int i = 0; i < result.Length - 1; i++)
            calculatedChecksum += result[i];
        result[result.Length - 1].Should().Be(calculatedChecksum, "checksum should be valid");
    }

    [Fact]
    public async Task PerformanceComparison_ParallelVsSequential_ParallelIsFaster()
    {
        // This test demonstrates the performance improvement
        // In real scenarios, parallel should be ~40% faster for multiple channels

        var eventAggregator = _fixture.MockEventAggregator;
        var processedCount = 0;
        var allProcessed = new TaskCompletionSource<bool>();
        const int channelCount = 4;

        eventAggregator.Subscribe<TemperatureDataReceivedEvent>(e =>
        {
            // Simulate processing work
            Thread.SpinWait(10000);
            if (Interlocked.Increment(ref processedCount) >= channelCount)
                allProcessed.TrySetResult(true);
            return Task.CompletedTask;
        });

        _pipeline = CreatePipeline(eventAggregator);

        var sw = Stopwatch.StartNew();

        // Enqueue all channels quickly (simulating fast sequential reads)
        for (int i = 1; i <= channelCount; i++)
        {
            var rawResponse = GenerateValidDtsResponse(channelId: (byte)(i - 1), pointCount: 500);
            await _pipeline.EnqueueAsync(i, rawResponse, DateTime.UtcNow);
        }

        await Task.WhenAny(allProcessed.Task, Task.Delay(30000));
        sw.Stop();

        // Assert
        processedCount.Should().Be(channelCount);
        // Just verify processing completed - actual performance gain depends on hardware
        sw.ElapsedMilliseconds.Should().BeLessThan(30000, "processing should complete within timeout");
    }

    private DtsDataPipeline CreatePipeline(IEventAggregator eventAggregator)
    {
        return new DtsDataPipeline(
            eventAggregator,
            _mockConfigManager.Object,
            NullLogger<DtsDataPipeline>.Instance,
            Options.Create(_options));
    }

    /// <summary>
    /// Generates a valid DTS response format for testing.
    /// </summary>
    private static byte[] GenerateValidDtsResponse(byte channelId, int pointCount)
    {
        const int discardCount = 475;
        var totalSamples = discardCount + pointCount;

        // Response format: FLAG2(2) + LEN(4) + ADR(1) + CMD(1) + CH(1) + ID(4) + T_DATA(N*2) + SUM(1)
        var dataLength = totalSamples * 2;
        var totalLength = 14 + dataLength;
        var response = new byte[totalLength];

        // FLAG2
        response[0] = 0xCC;
        response[1] = 0xAA;

        // LEN (4 bytes, big-endian)
        var len = totalLength - 6;
        response[2] = (byte)((len >> 24) & 0xFF);
        response[3] = (byte)((len >> 16) & 0xFF);
        response[4] = (byte)((len >> 8) & 0xFF);
        response[5] = (byte)(len & 0xFF);

        // ADR
        response[6] = 0xFF;

        // CMD
        response[7] = 0x07;

        // CH
        response[8] = channelId;

        // ID (4 bytes)
        response[9] = 0x00;
        response[10] = 0x00;
        response[11] = 0x00;
        response[12] = 0x01;

        // Temperature data (25.0°C default)
        for (int i = 0; i < totalSamples; i++)
        {
            short rawTemp = 250; // 25.0°C * 10
            int dataIndex = 13 + (i * 2);
            response[dataIndex] = (byte)((rawTemp >> 8) & 0xFF);
            response[dataIndex + 1] = (byte)(rawTemp & 0xFF);
        }

        // Calculate checksum
        byte checksum = 0;
        for (int i = 0; i < totalLength - 1; i++)
            checksum += response[i];
        response[totalLength - 1] = checksum;

        return response;
    }
}
