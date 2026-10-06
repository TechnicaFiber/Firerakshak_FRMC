using FireRakshak.FRMC.Core.Enums;
using FireRakshak.FRMC.Core.Events;
using FireRakshak.FRMC.Core.Models;
using FireRakshak.FRMC.IntegrationTests.Fixtures;
using FluentAssertions;
using Xunit;

namespace FireRakshak.FRMC.IntegrationTests.Tests;

/// <summary>
/// Integration tests for error handling and edge cases.
/// Tests system behavior under error conditions and boundary cases.
/// </summary>
[Collection("FRMC Integration Tests")]
public class ErrorHandlingTests
{
    private readonly FrmcTestFixture _fixture;

    public ErrorHandlingTests(FrmcTestFixture fixture)
    {
        _fixture = fixture;
        _fixture.Reset();
    }

    [Fact]
    public async Task DtsDisconnect_DuringRead_ThrowsException()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        await mockDts.ConnectAsync();

        // Act - Disconnect mid-operation
        mockDts.ForceDisconnect();

        // Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => mockDts.ReadChannelTemperatureAsync(1));
    }

    [Fact]
    public async Task RS232Stop_DuringOperation_ThrowsException()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        await mockRS232.StartAsync();
        await mockRS232.StopAsync();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => mockRS232.SendRelayCommandAsync(11, true));
    }

    [Fact]
    public async Task InvalidChannelId_WhenReading_HandlesGracefully()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        await mockDts.ConnectAsync();

        // Act - Read invalid channel (0 or > 8)
        // Mock should still return data for any channel ID
        var data = await mockDts.ReadChannelTemperatureAsync(0);

        // Assert - Should still return data (mock doesn't validate channel)
        data.Should().NotBeNull();
    }

    [Fact]
    public async Task InvalidRelayId_WhenSending_TracksCommand()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        await mockRS232.StartAsync();

        // Act - Send to relay ID outside valid range
        await mockRS232.SendRelayCommandAsync(255, true);

        // Assert - Mock tracks the command regardless
        var lastCommand = mockRS232.GetLastRelayCommand();
        lastCommand.Should().NotBeNull();
        lastCommand!.RelayId.Should().Be(255);
    }

    [Fact]
    public async Task EmptyTemperatureData_WhenFiberFullyBroken_HandlesGracefully()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        await mockDts.ConnectAsync();
        mockDts.SimulateFiberBreak = true;
        mockDts.FiberBreakPosition = -1; // Break before first point - all data invalid

        // Act
        var data = await mockDts.ReadChannelTemperatureAsync(1);

        // Assert - All data should be invalid (positions > -1)
        data.Should().NotBeEmpty();
        data.All(p => p.Temperature == -999).Should().BeTrue();
    }

    [Fact]
    public async Task ExtremeTemperature_HighValue_HandlesCorrectly()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        await mockDts.ConnectAsync();
        mockDts.SimulateHighTemperature = true;
        mockDts.HighTemperatureValue = 500; // Extreme value

        // Act
        var data = await mockDts.ReadChannelTemperatureAsync(1);

        // Assert
        var maxTemp = data.Max(p => p.Temperature);
        maxTemp.Should().Be(500);
    }

    [Fact]
    public async Task ExtremeTemperature_NegativeValue_HandlesCorrectly()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        await mockDts.ConnectAsync();
        mockDts.SimulateLowTemperature = true;
        mockDts.LowTemperatureValue = -50; // Extreme cold

        // Act
        var data = await mockDts.ReadChannelTemperatureAsync(1);

        // Assert
        var minTemp = data.Where(p => p.Temperature > -900).Min(p => p.Temperature);
        minTemp.Should().Be(-50);
    }

    [Fact]
    public async Task RapidRelayToggle_MultipleCommands_AllTracked()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        await mockRS232.StartAsync();

        // Act - Rapid toggle
        for (int i = 0; i < 10; i++)
        {
            await mockRS232.SendRelayCommandAsync(11, i % 2 == 0);
        }

        // Assert
        mockRS232.RelayCommands.Should().HaveCount(10);
        var relay11Commands = mockRS232.GetRelayCommandsForRelay(11);
        relay11Commands.Should().HaveCount(10);
    }

    [Fact]
    public async Task ConcurrentDtsReads_AllComplete()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        await mockDts.ConnectAsync();

        // Act - Concurrent reads
        var tasks = Enumerable.Range(1, 4)
            .Select(ch => mockDts.ReadChannelTemperatureAsync((byte)ch))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // Assert
        results.Should().HaveCount(4);
        results.All(r => r != null && r.Length > 0).Should().BeTrue();
        mockDts.ReadTemperatureCallCount.Should().Be(4);
    }

    [Fact]
    public async Task HealthCheck_WhenDtsUnresponsive_ReportsCorrectly()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        await mockDts.ConnectAsync();
        mockDts.SetDtsResponsive(false);

        // Act
        var health = await mockDts.ReadHealthParametersAsync();

        // Assert
        health.DtsResponsive.Should().BeFalse();
    }

    [Fact]
    public async Task HealthCheck_WhenPumpCurrentZero_ReportsLaserOff()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        await mockDts.ConnectAsync();
        mockDts.SetPumpCurrent(0);

        // Act
        var health = await mockDts.ReadHealthParametersAsync();

        // Assert
        health.PumpCurrent.Should().Be(0);
    }

    [Fact]
    public async Task ZeroLengthChannel_ReturnsEmptyData()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        await mockDts.ConnectAsync();
        await mockDts.ConfigureChannelLengthAsync(1, 0);

        // Act
        var data = await mockDts.ReadChannelTemperatureAsync(1);

        // Assert - With 0 length, no data points
        data.Should().BeEmpty();
    }

    [Fact]
    public async Task VeryLongChannel_ReturnsExpectedDataPoints()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        await mockDts.ConnectAsync();
        await mockDts.ConfigureChannelLengthAsync(1, 10000); // 10km

        // Act
        var data = await mockDts.ReadChannelTemperatureAsync(1);

        // Assert - At 0.4m resolution, 10000m = 25000 points
        data.Length.Should().Be(25000);
    }

    [Fact]
    public async Task MultipleResets_StateConsistent()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        var mockRS232 = _fixture.MockRS232;

        // First session
        await mockDts.ConnectAsync();
        await mockDts.ReadChannelTemperatureAsync(1);
        await mockRS232.StartAsync();
        await mockRS232.SendRelayCommandAsync(11, true);

        // Act - Multiple resets
        _fixture.Reset();
        _fixture.Reset();
        _fixture.Reset();

        // Assert - State is clean
        mockDts.IsConnected.Should().BeFalse();
        mockRS232.IsRunning.Should().BeFalse();
        mockDts.ReadTemperatureCallCount.Should().Be(0);
        mockRS232.RelayCommands.Should().BeEmpty();
    }

    [Fact]
    public async Task EventPublish_WithNullHandler_DoesNotThrow()
    {
        // Arrange
        var mockEvents = _fixture.MockEventAggregator;
        // No subscribers

        // Act & Assert - Should not throw
        await mockEvents.PublishAsync(new AlarmTriggeredEvent(
            1, 1, AlarmType.MaxTemperature, 95, DateTime.UtcNow, 100));

        mockEvents.GetEventCount<AlarmTriggeredEvent>().Should().Be(1);
    }

    [Fact]
    public async Task BoundaryTemperature_ExactlyAtThreshold_Detection()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        await mockDts.ConnectAsync();
        mockDts.SimulateHighTemperature = true;
        mockDts.HighTemperatureValue = 80.0; // Exactly at threshold

        // Act
        var data = await mockDts.ReadChannelTemperatureAsync(1);

        // Assert
        var maxTemp = data.Max(p => p.Temperature);
        maxTemp.Should().BeApproximately(80.0, 0.001);
    }

    [Fact]
    public async Task SequentialConnectDisconnect_WorksCorrectly()
    {
        // Arrange
        var mockDts = _fixture.MockDts;

        // Act & Assert - Multiple connect/disconnect cycles
        for (int i = 0; i < 5; i++)
        {
            await mockDts.ConnectAsync();
            mockDts.IsConnected.Should().BeTrue();

            var data = await mockDts.ReadChannelTemperatureAsync(1);
            data.Should().NotBeEmpty();

            await mockDts.DisconnectAsync();
            mockDts.IsConnected.Should().BeFalse();
        }
    }
}
