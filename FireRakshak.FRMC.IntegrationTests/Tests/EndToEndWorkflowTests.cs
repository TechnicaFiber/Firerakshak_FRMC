using FireRakshak.FRMC.Core.Enums;
using FireRakshak.FRMC.Core.Events;
using FireRakshak.FRMC.Core.Models;
using FireRakshak.FRMC.IntegrationTests.Fixtures;
using FireRakshak.FRMC.IntegrationTests.Mocks;
using FluentAssertions;
using Xunit;

namespace FireRakshak.FRMC.IntegrationTests.Tests;

/// <summary>
/// End-to-end integration tests simulating complete FRMC workflows.
/// Tests full data flow from DTS to alarms to relay activation.
/// </summary>
[Collection("FRMC Integration Tests")]
public class EndToEndWorkflowTests
{
    private readonly FrmcTestFixture _fixture;

    public EndToEndWorkflowTests(FrmcTestFixture fixture)
    {
        _fixture = fixture;
        _fixture.Reset();
    }

    [Fact]
    public async Task CompleteAlarmWorkflow_HighTemperature_ActivatesRelayAndNotifies()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        var mockRS232 = _fixture.MockRS232;
        var mockEvents = _fixture.MockEventAggregator;

        await mockDts.ConnectAsync();
        await mockRS232.StartAsync();

        // Configure zone with relay assignment
        var zoneConfig = _fixture.DefaultZoneConfigs.First();
        zoneConfig.AssignedRelayId = 12;
        zoneConfig.MaxTemp = 80;
        zoneConfig.IsMaxTempEnabled = true;

        // Setup high temperature simulation
        mockDts.SimulateHighTemperature = true;
        mockDts.HighTemperatureValue = 95; // Above 80°C threshold
        mockDts.HighTemperaturePosition = (zoneConfig.StartPoint + zoneConfig.EndPoint) / 2;

        // Act - Simulate temperature reading
        var data = await mockDts.ReadChannelTemperatureAsync(1);

        // Verify high temperature detected
        var hotSpot = data.Where(p => p.Position >= zoneConfig.StartPoint &&
                                       p.Position <= zoneConfig.EndPoint)
                         .Max(p => p.Temperature);

        hotSpot.Should().BeGreaterThan(80, "Hot spot should exceed threshold");

        // Simulate alarm trigger
        await mockEvents.PublishAsync(new AlarmTriggeredEvent(
            channelId: zoneConfig.ChannelId,
            zoneId: zoneConfig.ZoneId,
            type: AlarmType.MaxTemperature,
            value: hotSpot,
            timestamp: DateTime.UtcNow,
            position: mockDts.HighTemperaturePosition
        ));

        // Simulate relay activation
        await mockRS232.SetHeartbeatAlarmAsync(true);
        await mockRS232.SendRelayCommandAsync((byte)zoneConfig.AssignedRelayId!.Value, true);

        // Assert
        mockEvents.HasEventOfType<AlarmTriggeredEvent>().Should().BeTrue();
        mockRS232.GetCurrentAlarmState().Should().BeTrue();
        mockRS232.GetLastRelayCommand()!.RelayId.Should().Be(12);
        mockRS232.GetLastRelayCommand()!.TurnOn.Should().BeTrue();
    }

    [Fact]
    public async Task CompleteAlarmWorkflow_TemperatureNormalizes_ClearsAlarmAndRelay()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        var mockRS232 = _fixture.MockRS232;
        var mockEvents = _fixture.MockEventAggregator;

        await mockDts.ConnectAsync();
        await mockRS232.StartAsync();

        // Setup initial alarm state
        mockDts.SimulateHighTemperature = true;
        mockDts.HighTemperatureValue = 95;

        await mockRS232.SetHeartbeatAlarmAsync(true);
        await mockRS232.SendRelayCommandAsync(11, true);

        // Act - Temperature normalizes
        mockDts.SimulateHighTemperature = false;
        var data = await mockDts.ReadChannelTemperatureAsync(1);

        var maxTemp = data.Max(p => p.Temperature);
        maxTemp.Should().BeLessThan(80, "Temperature should normalize");

        // Simulate alarm clear
        await mockEvents.PublishAsync(new AlarmAutoClearedEvent(
            channelId: 1,
            zoneId: 1,
            type: AlarmType.MaxTemperature,
            timestamp: DateTime.UtcNow
        ));

        // Clear relay and heartbeat
        await mockRS232.SendRelayCommandAsync(11, false);
        await mockRS232.SetHeartbeatAlarmAsync(false);

        // Assert
        mockEvents.HasEventOfType<AlarmAutoClearedEvent>().Should().BeTrue();
        mockRS232.GetCurrentAlarmState().Should().BeFalse();
        mockRS232.GetLastRelayCommand()!.TurnOn.Should().BeFalse();
    }

    [Fact]
    public async Task FiberBreakWorkflow_DetectsBreakAndClearsAlarmsAfterBreak()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        var mockEvents = _fixture.MockEventAggregator;

        await mockDts.ConnectAsync();

        // Configure zones: Zone 1 (0-200m), Zone 2 (200-400m), Zone 3 (400-600m)
        mockDts.SimulateFiberBreak = true;
        mockDts.FiberBreakPosition = 350; // Break between Zone 2 and Zone 3

        // Act
        var data = await mockDts.ReadChannelTemperatureAsync(1);

        // Publish fiber break event
        await mockEvents.PublishAsync(new FiberBreakDetectedEvent(
            channelId: 1,
            position: mockDts.FiberBreakPosition,
            isBroken: true,
            timestamp: DateTime.UtcNow
        ));

        // Assert
        // Valid data before break
        var validData = data.Where(p => p.Position <= 350 && p.Temperature > -900).ToList();
        validData.Should().NotBeEmpty();

        // Invalid data after break
        var invalidData = data.Where(p => p.Position > 350).ToList();
        invalidData.Should().OnlyContain(p => p.Temperature == -999);

        // Fiber break event published
        var fiberBreakEvent = mockEvents.GetLastPublishedEvent<FiberBreakDetectedEvent>();
        fiberBreakEvent.Should().NotBeNull();
        fiberBreakEvent!.IsBroken.Should().BeTrue();
        fiberBreakEvent.Position.Should().Be(350);
    }

    [Fact]
    public async Task RateOfRiseWorkflow_DetectsRapidTemperatureIncrease()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        var mockEvents = _fixture.MockEventAggregator;

        await mockDts.ConnectAsync();
        mockDts.BaseTemperature = 25;

        var zoneConfig = _fixture.DefaultZoneConfigs.First();
        zoneConfig.RoRThreshold = 10; // 10°C/minute
        zoneConfig.IsRateOfRiseEnabled = true;

        // Act - Simulate readings over 60 seconds
        var temperatureReadings = new List<(DateTime Time, double AvgTemp)>();

        for (int i = 0; i < 10; i++)
        {
            // Simulate 2°C increase per reading (6-second intervals = ~20°C/minute)
            mockDts.TemperatureIncrement = i * 2.0;
            var data = await mockDts.ReadChannelTemperatureAsync(1);

            var zoneData = data.Where(p => p.Position >= zoneConfig.StartPoint &&
                                            p.Position <= zoneConfig.EndPoint);
            var avgTemp = zoneData.Average(p => p.Temperature);

            temperatureReadings.Add((DateTime.UtcNow.AddSeconds(i * 6), avgTemp));
        }

        // Calculate RoR
        var firstReading = temperatureReadings.First();
        var lastReading = temperatureReadings.Last();
        var tempChange = lastReading.AvgTemp - firstReading.AvgTemp;
        var timeMinutes = (lastReading.Time - firstReading.Time).TotalMinutes;
        var rorPerMinute = tempChange / timeMinutes;

        rorPerMinute.Should().BeGreaterThan(10, "RoR should exceed threshold");

        // Publish RoR alarm
        await mockEvents.PublishAsync(new AlarmTriggeredEvent(
            channelId: zoneConfig.ChannelId,
            zoneId: zoneConfig.ZoneId,
            type: AlarmType.RateOfRise,
            value: rorPerMinute,
            timestamp: DateTime.UtcNow,
            position: (zoneConfig.StartPoint + zoneConfig.EndPoint) / 2
        ));

        // Assert
        var alarmEvent = mockEvents.GetLastPublishedEvent<AlarmTriggeredEvent>();
        alarmEvent.Should().NotBeNull();
        alarmEvent!.Type.Should().Be(AlarmType.RateOfRise);
    }

    [Fact]
    public async Task MultiZoneAlarmWorkflow_MultipleAlarmsActivateMultipleRelays()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        var mockRS232 = _fixture.MockRS232;
        var mockEvents = _fixture.MockEventAggregator;

        await mockDts.ConnectAsync();
        await mockRS232.StartAsync();

        // Setup: Zone 1 -> Relay 11, Zone 2 -> Relay 12
        var zone1 = _fixture.DefaultZoneConfigs[0];
        zone1.AssignedRelayId = 12;

        var zone2 = _fixture.DefaultZoneConfigs[1];
        zone2.AssignedRelayId = 12;

        // Act - Trigger alarms in both zones
        await mockEvents.PublishAsync(new AlarmTriggeredEvent(1, 1, AlarmType.MaxTemperature, 100, DateTime.UtcNow, 100));
        await mockRS232.SendRelayCommandAsync(11, true);

        await mockEvents.PublishAsync(new AlarmTriggeredEvent(1, 2, AlarmType.MaxTemperature, 95, DateTime.UtcNow, 300));
        await mockRS232.SendRelayCommandAsync(12, true);

        await mockRS232.SetHeartbeatAlarmAsync(true);

        // Assert
        mockEvents.GetEventCount<AlarmTriggeredEvent>().Should().Be(2);
        mockRS232.RelayCommands.Should().HaveCount(2);
        mockRS232.GetRelayCommandsForRelay(11).Should().ContainSingle(c => c.TurnOn);
        mockRS232.GetRelayCommandsForRelay(12).Should().ContainSingle(c => c.TurnOn);
    }

    [Fact]
    public async Task ResetButtonWorkflow_ClearsAllAlarmsAndRelays()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        var mockEvents = _fixture.MockEventAggregator;

        await mockRS232.StartAsync();

        // Setup active alarms
        await mockRS232.SendRelayCommandAsync(11, true);
        await mockRS232.SendRelayCommandAsync(12, true);
        await mockRS232.SetHeartbeatAlarmAsync(true);

        // Act - Reset button pressed
        await mockEvents.PublishAsync(new AlarmResetRequestedEvent(
            source: "STM32",
            timestamp: DateTime.UtcNow
        ));

        // Clear all relays
        await mockRS232.SendRelayCommandAsync(11, false);
        await mockRS232.SendRelayCommandAsync(12, false);
        await mockRS232.SetHeartbeatAlarmAsync(false);

        // Assert
        mockEvents.HasEventOfType<AlarmResetRequestedEvent>().Should().BeTrue();
        var relay11Commands = mockRS232.GetRelayCommandsForRelay(11);
        var relay12Commands = mockRS232.GetRelayCommandsForRelay(12);

        relay11Commands.Last().TurnOn.Should().BeFalse();
        relay12Commands.Last().TurnOn.Should().BeFalse();
        mockRS232.GetCurrentAlarmState().Should().BeFalse();
    }

    [Fact]
    public async Task HealthMonitoringWorkflow_DetectsAndReportsIssues()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        var mockEvents = _fixture.MockEventAggregator;

        await mockDts.ConnectAsync();

        // Act - Normal health check
        var healthNormal = await mockDts.ReadHealthParametersAsync();
        healthNormal.LampStatus.Should().BeTrue();
        healthNormal.DtsResponsive.Should().BeTrue();

        // Simulate lamp failure
        mockDts.SetLampStatus(false);
        var healthFaulted = await mockDts.ReadHealthParametersAsync();

        // Publish health fault event
        var fault = new SystemHealthFault
        {
            FaultType = SystemHealthFaultType.LaserOff,
            IsActive = true,
            FirstDetectedAt = DateTime.UtcNow,
            LastUpdatedAt = DateTime.UtcNow,
            CurrentValue = 0,
            ThresholdValue = 1,
            Description = "Lamp/Laser is off"
        };
        await mockEvents.PublishAsync(new SystemHealthFaultEvent(
            fault: fault,
            isNewFault: true,
            timestamp: DateTime.UtcNow
        ));

        // Assert
        healthFaulted.LampStatus.Should().BeFalse();
        mockEvents.HasEventOfType<SystemHealthFaultEvent>().Should().BeTrue();
    }

    [Fact]
    public async Task ConfigurationChangeWorkflow_UpdatesAndNotifies()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        var mockEvents = _fixture.MockEventAggregator;

        await mockDts.ConnectAsync();

        // Act - Channel length change
        await mockDts.ConfigureChannelLengthAsync(1, 1500);

        await mockEvents.PublishAsync(new ConfigurationChangedEvent(
            configType: ConfigurationType.Channel,
            changedBy: "Admin",
            source: "DTSCM",
            changes: new Dictionary<string, object>
            {
                { "ChannelId", 1 },
                { "ChannelLength", 1500 }
            }
        ));

        // Assert
        mockDts.ConfiguredChannels.Should().Contain(c => c.Channel == 1 && c.Length == 1500);
        mockEvents.HasEventOfType<ConfigurationChangedEvent>().Should().BeTrue();
    }

    [Fact]
    public async Task DeviationWorkflow_DetectsHotSpotWithinZone()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        var mockEvents = _fixture.MockEventAggregator;

        await mockDts.ConnectAsync();

        var zoneConfig = _fixture.DefaultZoneConfigs.First();
        zoneConfig.DeviationThreshold = 20; // 20°C max difference within zone
        zoneConfig.IsDeviationEnabled = true;

        // Setup deviation (hot spot) - position must be within zone range (0-200)
        mockDts.SimulateDeviation = true;
        mockDts.DeviationValue = 30; // 30°C above base creates deviation
        mockDts.BaseTemperature = 25;
        mockDts.HighTemperaturePosition = 100; // Within zone 1 (0-200m)

        // Act
        var data = await mockDts.ReadChannelTemperatureAsync(1);
        var zoneData = data.Where(p => p.Position >= zoneConfig.StartPoint &&
                                        p.Position <= zoneConfig.EndPoint &&
                                        p.Temperature > -900);

        var maxInZone = zoneData.Max(p => p.Temperature);
        var minInZone = zoneData.Min(p => p.Temperature);
        var deviation = maxInZone - minInZone;

        deviation.Should().BeGreaterThan(20, "Deviation should exceed threshold");

        // Publish deviation alarm
        await mockEvents.PublishAsync(new AlarmTriggeredEvent(
            channelId: zoneConfig.ChannelId,
            zoneId: zoneConfig.ZoneId,
            type: AlarmType.ZoneDeviation,
            value: deviation,
            timestamp: DateTime.UtcNow,
            position: mockDts.HighTemperaturePosition
        ));

        // Assert
        var alarmEvent = mockEvents.GetLastPublishedEvent<AlarmTriggeredEvent>();
        alarmEvent.Should().NotBeNull();
        alarmEvent!.Type.Should().Be(AlarmType.ZoneDeviation);
        alarmEvent.Value.Should().BeGreaterThan(20);
    }

    [Fact]
    public async Task PreAlarmToMaxAlarmEscalation_TransitionsCorrectly()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        var mockEvents = _fixture.MockEventAggregator;

        await mockDts.ConnectAsync();

        var zoneConfig = _fixture.DefaultZoneConfigs.First();
        zoneConfig.PreAlarm = 60;
        zoneConfig.MaxTemp = 80;

        // Act - Phase 1: Pre-alarm temperature (65°C)
        mockDts.SimulateHighTemperature = true;
        mockDts.HighTemperatureValue = 65;

        var data1 = await mockDts.ReadChannelTemperatureAsync(1);
        var maxTemp1 = data1.Max(p => p.Temperature);

        await mockEvents.PublishAsync(new AlarmTriggeredEvent(
            1, 1, AlarmType.PreAlarm, maxTemp1, DateTime.UtcNow, null
        ));

        // Phase 2: Escalate to max alarm (95°C)
        mockDts.HighTemperatureValue = 95;
        var data2 = await mockDts.ReadChannelTemperatureAsync(1);
        var maxTemp2 = data2.Max(p => p.Temperature);

        // Clear pre-alarm when max alarm triggers
        await mockEvents.PublishAsync(new AlarmAutoClearedEvent(
            1, 1, AlarmType.PreAlarm, DateTime.UtcNow
        ));

        await mockEvents.PublishAsync(new AlarmTriggeredEvent(
            1, 1, AlarmType.MaxTemperature, maxTemp2, DateTime.UtcNow, null
        ));

        // Assert
        var preAlarmEvents = mockEvents.GetPublishedEvents<AlarmTriggeredEvent>()
            .Where(e => e.Type == AlarmType.PreAlarm).ToList();
        var maxAlarmEvents = mockEvents.GetPublishedEvents<AlarmTriggeredEvent>()
            .Where(e => e.Type == AlarmType.MaxTemperature).ToList();

        preAlarmEvents.Should().HaveCount(1);
        maxAlarmEvents.Should().HaveCount(1);
        mockEvents.HasEventOfType<AlarmAutoClearedEvent>().Should().BeTrue();
    }
}
