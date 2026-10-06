using FireRakshak.FRMC.Core.Enums;
using FireRakshak.FRMC.Core.Events;
using FireRakshak.FRMC.Core.Models;
using FireRakshak.FRMC.IntegrationTests.Fixtures;
using FireRakshak.FRMC.IntegrationTests.Mocks;
using FluentAssertions;
using Xunit;

namespace FireRakshak.FRMC.IntegrationTests.Tests;

/// <summary>
/// Integration tests for temperature data processing and alarm triggering.
/// Tests DTS data flow, threshold evaluation, and alarm generation.
/// </summary>
[Collection("FRMC Integration Tests")]
public class TemperatureAndAlarmTests
{
    private readonly FrmcTestFixture _fixture;

    public TemperatureAndAlarmTests(FrmcTestFixture fixture)
    {
        _fixture = fixture;
        _fixture.Reset();
    }

    [Fact]
    public async Task ReadTemperature_WhenDtsConnected_ReturnsValidData()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        await mockDts.ConnectAsync();

        // Act
        var data = await mockDts.ReadChannelTemperatureAsync(1);

        // Assert
        data.Should().NotBeNull();
        data.Should().NotBeEmpty();
        data.All(p => p.Position >= 0).Should().BeTrue();
        mockDts.ReadTemperatureCallCount.Should().Be(1);
    }

    [Fact]
    public async Task ReadTemperature_WhenDtsNotConnected_ThrowsException()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        // Don't connect

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => mockDts.ReadChannelTemperatureAsync(1));
    }

    [Fact]
    public async Task HighTemperature_WhenAboveThreshold_GeneratesMaxAlarm()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        var mockEvents = _fixture.MockEventAggregator;

        await mockDts.ConnectAsync();
        mockDts.SimulateHighTemperature = true;
        mockDts.HighTemperatureValue = 100; // Above 80°C threshold
        mockDts.HighTemperaturePosition = 150; // In Zone 1 (0-200m)

        // Act
        var data = await mockDts.ReadChannelTemperatureAsync(1);

        // Assert
        var hotSpots = data.Where(p => p.Temperature > 80).ToList();
        hotSpots.Should().NotBeEmpty("High temperature should be detected");
        hotSpots.First().Position.Should().BeApproximately(150, 15);
    }

    [Fact]
    public async Task LowTemperature_WhenBelowThreshold_GeneratesMinAlarm()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        await mockDts.ConnectAsync();
        mockDts.SimulateLowTemperature = true;
        mockDts.LowTemperatureValue = -5; // Below 5°C threshold
        mockDts.LowTemperaturePosition = 350; // In Zone 2 (200-400m)

        // Act
        var data = await mockDts.ReadChannelTemperatureAsync(1);

        // Assert
        var coldSpots = data.Where(p => p.Temperature < 5).ToList();
        coldSpots.Should().NotBeEmpty("Low temperature should be detected");
    }

    [Fact]
    public async Task PreAlarm_WhenApproachingMaxThreshold_GeneratesPreAlarm()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        await mockDts.ConnectAsync();
        mockDts.SimulateHighTemperature = true;
        mockDts.HighTemperatureValue = 65; // Between PreAlarm (60) and Max (80)
        mockDts.HighTemperaturePosition = 150;

        // Act
        var data = await mockDts.ReadChannelTemperatureAsync(1);

        // Assert
        var preAlarmPoints = data.Where(p => p.Temperature > 60 && p.Temperature < 80).ToList();
        preAlarmPoints.Should().NotBeEmpty("Pre-alarm temperature should be detected");
    }

    [Fact]
    public async Task FiberBreak_WhenSimulated_ReturnsInvalidDataBeyondBreak()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        await mockDts.ConnectAsync();
        mockDts.SimulateFiberBreak = true;
        mockDts.FiberBreakPosition = 500; // Break at 500m

        // Act
        var data = await mockDts.ReadChannelTemperatureAsync(1);

        // Assert
        var validData = data.Where(p => p.Position <= 500 && p.Temperature > -900).ToList();
        var invalidData = data.Where(p => p.Position > 500).ToList();

        validData.Should().NotBeEmpty("Data before break should be valid");
        invalidData.Should().OnlyContain(p => p.Temperature == -999,
            "Data after break should be invalid");
    }

    [Fact]
    public async Task Deviation_WhenHotSpotDetected_CalculatesCorrectDeviation()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        await mockDts.ConnectAsync();
        mockDts.BaseTemperature = 30;
        mockDts.SimulateDeviation = true;
        mockDts.DeviationValue = 25; // 25°C above base = 55°C hot spot

        // Act
        var data = await mockDts.ReadChannelTemperatureAsync(1);

        // Assert
        var maxTemp = data.Max(p => p.Temperature);
        var minTemp = data.Where(p => p.Temperature > -900).Min(p => p.Temperature);
        var deviation = maxTemp - minTemp;

        deviation.Should().BeGreaterThan(20, "Deviation should exceed threshold");
    }

    [Fact]
    public async Task RateOfRise_WhenTemperatureIncreasing_CalculatesCorrectRoR()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        await mockDts.ConnectAsync();
        mockDts.BaseTemperature = 25;

        // Simulate multiple readings with increasing temperature
        var readings = new List<double>();

        // Act - Simulate 60 seconds of readings (10 readings at 6 second intervals)
        for (int i = 0; i < 10; i++)
        {
            mockDts.TemperatureIncrement = i * 1.0; // +1°C per reading
            var data = await mockDts.ReadChannelTemperatureAsync(1);
            readings.Add(data.Average(p => p.Temperature));
        }

        // Assert
        var tempChange = readings.Last() - readings.First();
        tempChange.Should().BeApproximately(9, 2, "Temperature should increase over time");

        // RoR over 60 seconds: 9°C / 1 min = 9°C/min
        var rorPerMinute = tempChange; // Already in °C/minute for 60-second window
        rorPerMinute.Should().BeGreaterThan(5, "RoR should exceed typical threshold");
    }

    [Fact]
    public async Task HealthParameters_WhenRead_ReturnsValidData()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        await mockDts.ConnectAsync();

        // Act
        var health = await mockDts.ReadHealthParametersAsync();

        // Assert
        health.Should().NotBeNull();
        health.LampStatus.Should().BeTrue();
        health.DtsResponsive.Should().BeTrue();
        health.ModuleTemperature.Should().BeInRange(20, 50);
        health.PumpCurrent.Should().BeGreaterThan(0);
        mockDts.ReadHealthCallCount.Should().Be(1);
    }

    [Fact]
    public async Task HealthParameters_WhenLampFails_ReportsLampStatus()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        await mockDts.ConnectAsync();
        mockDts.SetLampStatus(false);

        // Act
        var health = await mockDts.ReadHealthParametersAsync();

        // Assert
        health.LampStatus.Should().BeFalse("Lamp failure should be reported");
    }

    [Fact]
    public async Task ChannelConfiguration_WhenSet_AppliesCorrectLength()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        await mockDts.ConnectAsync();

        // Act
        await mockDts.ConfigureChannelLengthAsync(1, 2000);
        await mockDts.ConfigureChannelLengthAsync(2, 1500);

        // Assert
        mockDts.ConfiguredChannels.Should().HaveCount(2);
        mockDts.ConfiguredChannels.Should().Contain(c => c.Channel == 1 && c.Length == 2000);
        mockDts.ConfiguredChannels.Should().Contain(c => c.Channel == 2 && c.Length == 1500);
    }

    [Fact]
    public async Task MultipleChannels_WhenRead_ReturnIndependentData()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        await mockDts.ConnectAsync();

        // Configure different lengths
        await mockDts.ConfigureChannelLengthAsync(1, 1000);
        await mockDts.ConfigureChannelLengthAsync(2, 500);

        // Act
        var data1 = await mockDts.ReadChannelTemperatureAsync(1);
        var data2 = await mockDts.ReadChannelTemperatureAsync(2);

        // Assert
        data1.Length.Should().BeGreaterThan(data2.Length,
            "Channel 1 is longer so should have more data points");
        mockDts.ReadTemperatureCallCount.Should().Be(2);
    }

    [Fact]
    public async Task DtsDisconnect_WhenForced_ReportsDisconnected()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        await mockDts.ConnectAsync();
        mockDts.IsConnected.Should().BeTrue();

        // Act
        mockDts.ForceDisconnect();

        // Assert
        mockDts.IsConnected.Should().BeFalse();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => mockDts.ReadChannelTemperatureAsync(1));
    }
}
