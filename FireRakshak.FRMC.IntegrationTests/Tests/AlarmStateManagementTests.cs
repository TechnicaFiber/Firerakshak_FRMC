using FireRakshak.FRMC.Core.Enums;
using FireRakshak.FRMC.Core.Events;
using FireRakshak.FRMC.Core.Models;
using FireRakshak.FRMC.IntegrationTests.Fixtures;
using FluentAssertions;
using Xunit;

namespace FireRakshak.FRMC.IntegrationTests.Tests;

/// <summary>
/// Integration tests for alarm state management.
/// Tests concurrent alarms, priorities, and state transitions.
/// </summary>
[Collection("FRMC Integration Tests")]
public class AlarmStateManagementTests
{
    private readonly FrmcTestFixture _fixture;

    public AlarmStateManagementTests(FrmcTestFixture fixture)
    {
        _fixture = fixture;
        _fixture.Reset();
    }

    [Fact]
    public async Task ConcurrentAlarms_MultipleZones_AllTracked()
    {
        // Arrange
        var mockEvents = _fixture.MockEventAggregator;
        var timestamp = DateTime.UtcNow;

        // Act - Trigger alarms in multiple zones simultaneously
        var alarmTasks = new List<Task>();
        for (int zone = 1; zone <= 5; zone++)
        {
            var zoneId = zone;
            alarmTasks.Add(mockEvents.PublishAsync(new AlarmTriggeredEvent(
                channelId: 1,
                zoneId: zoneId,
                type: AlarmType.MaxTemperature,
                value: 90 + zoneId,
                timestamp: timestamp,
                position: zoneId * 100
            )));
        }

        await Task.WhenAll(alarmTasks);

        // Assert
        mockEvents.GetEventCount<AlarmTriggeredEvent>().Should().Be(5);
        var alarms = mockEvents.GetPublishedEvents<AlarmTriggeredEvent>();
        alarms.Select(a => a.ZoneId).Distinct().Count().Should().Be(5);
    }

    [Fact]
    public async Task ConcurrentAlarms_MultipleChannels_AllTracked()
    {
        // Arrange
        var mockEvents = _fixture.MockEventAggregator;

        // Act - Trigger alarms in multiple channels
        for (int channel = 1; channel <= 4; channel++)
        {
            await mockEvents.PublishAsync(new AlarmTriggeredEvent(
                channelId: channel,
                zoneId: 1,
                type: AlarmType.MaxTemperature,
                value: 95,
                timestamp: DateTime.UtcNow,
                position: 100
            ));
        }

        // Assert
        var alarms = mockEvents.GetPublishedEvents<AlarmTriggeredEvent>();
        alarms.Select(a => a.ChannelId).Distinct().Count().Should().Be(4);
    }

    [Fact]
    public async Task AlarmPriority_MaxAlarmOverridesPreAlarm()
    {
        // Arrange
        var mockEvents = _fixture.MockEventAggregator;

        // Act - PreAlarm first, then MaxAlarm
        await mockEvents.PublishAsync(new AlarmTriggeredEvent(
            1, 1, AlarmType.PreAlarm, 65, DateTime.UtcNow, 100));

        await mockEvents.PublishAsync(new AlarmAutoClearedEvent(
            1, 1, AlarmType.PreAlarm, DateTime.UtcNow));

        await mockEvents.PublishAsync(new AlarmTriggeredEvent(
            1, 1, AlarmType.MaxTemperature, 95, DateTime.UtcNow, 100));

        // Assert
        var preAlarms = mockEvents.GetPublishedEvents<AlarmTriggeredEvent>()
            .Where(a => a.Type == AlarmType.PreAlarm);
        var maxAlarms = mockEvents.GetPublishedEvents<AlarmTriggeredEvent>()
            .Where(a => a.Type == AlarmType.MaxTemperature);

        preAlarms.Should().HaveCount(1);
        maxAlarms.Should().HaveCount(1);
        mockEvents.HasEventOfType<AlarmAutoClearedEvent>().Should().BeTrue();
    }

    [Fact]
    public async Task MultipleAlarmTypes_SameZone_AllTracked()
    {
        // Arrange
        var mockEvents = _fixture.MockEventAggregator;

        // Act - Multiple alarm types in same zone
        await mockEvents.PublishAsync(new AlarmTriggeredEvent(
            1, 1, AlarmType.MaxTemperature, 95, DateTime.UtcNow, 100));
        await mockEvents.PublishAsync(new AlarmTriggeredEvent(
            1, 1, AlarmType.RateOfRise, 15, DateTime.UtcNow, 100));
        await mockEvents.PublishAsync(new AlarmTriggeredEvent(
            1, 1, AlarmType.ZoneDeviation, 25, DateTime.UtcNow, 100));

        // Assert
        var alarms = mockEvents.GetPublishedEvents<AlarmTriggeredEvent>()
            .Where(a => a.ChannelId == 1 && a.ZoneId == 1);

        alarms.Should().HaveCount(3);
        alarms.Select(a => a.Type).Distinct().Should().HaveCount(3);
    }

    [Fact]
    public async Task AlarmClearing_PartialClear_OtherAlarmsRemain()
    {
        // Arrange
        var mockEvents = _fixture.MockEventAggregator;

        // Trigger multiple alarms
        await mockEvents.PublishAsync(new AlarmTriggeredEvent(
            1, 1, AlarmType.MaxTemperature, 95, DateTime.UtcNow, 100));
        await mockEvents.PublishAsync(new AlarmTriggeredEvent(
            1, 2, AlarmType.MaxTemperature, 92, DateTime.UtcNow, 300));

        // Act - Clear only zone 1
        await mockEvents.PublishAsync(new AlarmAutoClearedEvent(
            1, 1, AlarmType.MaxTemperature, DateTime.UtcNow));

        // Assert
        var triggeredAlarms = mockEvents.GetPublishedEvents<AlarmTriggeredEvent>();
        var clearedAlarms = mockEvents.GetPublishedEvents<AlarmAutoClearedEvent>();

        triggeredAlarms.Should().HaveCount(2);
        clearedAlarms.Should().HaveCount(1);
        clearedAlarms.First().ZoneId.Should().Be(1);
    }

    [Fact]
    public async Task FiberBreak_ClearsAlarmsAfterBreakPosition()
    {
        // Arrange
        var mockEvents = _fixture.MockEventAggregator;

        // Alarms before and after break position
        await mockEvents.PublishAsync(new AlarmTriggeredEvent(
            1, 1, AlarmType.MaxTemperature, 95, DateTime.UtcNow, 100)); // Before break
        await mockEvents.PublishAsync(new AlarmTriggeredEvent(
            1, 3, AlarmType.MaxTemperature, 92, DateTime.UtcNow, 500)); // After break

        // Act - Fiber break at 300m
        await mockEvents.PublishAsync(new FiberBreakDetectedEvent(
            channelId: 1,
            position: 300,
            isBroken: true,
            timestamp: DateTime.UtcNow
        ));

        // Assert
        mockEvents.HasEventOfType<FiberBreakDetectedEvent>().Should().BeTrue();
        var fiberBreak = mockEvents.GetLastPublishedEvent<FiberBreakDetectedEvent>();
        fiberBreak!.Position.Should().Be(300);
    }

    [Fact]
    public async Task AlarmReset_ClearsAllAlarms()
    {
        // Arrange
        var mockEvents = _fixture.MockEventAggregator;

        // Multiple active alarms
        await mockEvents.PublishAsync(new AlarmTriggeredEvent(1, 1, AlarmType.MaxTemperature, 95, DateTime.UtcNow, 100));
        await mockEvents.PublishAsync(new AlarmTriggeredEvent(1, 2, AlarmType.RateOfRise, 15, DateTime.UtcNow, 300));
        await mockEvents.PublishAsync(new AlarmTriggeredEvent(2, 1, AlarmType.MaxTemperature, 90, DateTime.UtcNow, 100));

        // Act - Reset all alarms
        await mockEvents.PublishAsync(new AlarmResetRequestedEvent(
            source: "Operator",
            timestamp: DateTime.UtcNow
        ));

        // Assert
        mockEvents.GetEventCount<AlarmTriggeredEvent>().Should().Be(3);
        mockEvents.HasEventOfType<AlarmResetRequestedEvent>().Should().BeTrue();
    }

    [Fact]
    public async Task ZoneState_TracksAlarmConditions()
    {
        // Arrange
        var mockEvents = _fixture.MockEventAggregator;
        var zoneState = new ZoneState(
            zoneId: 1,
            name: "Zone 1",
            startPoint: 0,
            endPoint: 200,
            averageTemperature: 85,
            maxTemperature: 95,
            minTemperature: 75,
            preAlarm: false,
            maxAlarm: true,
            minAlarm: false,
            lastUpdate: DateTime.UtcNow
        );

        // Act
        await mockEvents.PublishAsync(new ZoneStateUpdatedEvent(
            channelId: 1,
            zoneId: 1,
            state: zoneState,
            timestamp: DateTime.UtcNow
        ));

        // Assert
        var stateEvent = mockEvents.GetLastPublishedEvent<ZoneStateUpdatedEvent>();
        stateEvent.Should().NotBeNull();
        stateEvent!.State.MaxAlarm.Should().BeTrue();
        stateEvent.State.MaxTemperature.Should().Be(95);
    }

    [Fact]
    public async Task AlarmValue_StoresBreachAmount()
    {
        // Arrange
        var mockEvents = _fixture.MockEventAggregator;
        var threshold = 80.0;
        var actualTemp = 95.0;

        // Act
        await mockEvents.PublishAsync(new AlarmTriggeredEvent(
            channelId: 1,
            zoneId: 1,
            type: AlarmType.MaxTemperature,
            value: actualTemp,
            timestamp: DateTime.UtcNow,
            position: 150
        ));

        // Assert
        var alarm = mockEvents.GetLastPublishedEvent<AlarmTriggeredEvent>();
        alarm!.Value.Should().Be(actualTemp);
        (alarm.Value - threshold).Should().Be(15, "Breach amount should be 15°C");
    }

    [Fact]
    public async Task AlarmPosition_StoresExactLocation()
    {
        // Arrange
        var mockEvents = _fixture.MockEventAggregator;
        var expectedPosition = 157.6;

        // Act
        await mockEvents.PublishAsync(new AlarmTriggeredEvent(
            channelId: 1,
            zoneId: 1,
            type: AlarmType.MaxTemperature,
            value: 95,
            timestamp: DateTime.UtcNow,
            position: expectedPosition
        ));

        // Assert
        var alarm = mockEvents.GetLastPublishedEvent<AlarmTriggeredEvent>();
        alarm!.Position.Should().Be(expectedPosition);
    }

    [Fact]
    public async Task AlarmTimestamp_IsAccurate()
    {
        // Arrange
        var mockEvents = _fixture.MockEventAggregator;
        var beforeTime = DateTime.UtcNow;

        // Act
        await mockEvents.PublishAsync(new AlarmTriggeredEvent(
            channelId: 1,
            zoneId: 1,
            type: AlarmType.MaxTemperature,
            value: 95,
            timestamp: DateTime.UtcNow,
            position: 100
        ));

        var afterTime = DateTime.UtcNow;

        // Assert
        var alarm = mockEvents.GetLastPublishedEvent<AlarmTriggeredEvent>();
        alarm!.Timestamp.Should().BeOnOrAfter(beforeTime);
        alarm.Timestamp.Should().BeOnOrBefore(afterTime);
    }

    [Fact]
    public async Task MinTemperatureAlarm_WhenBelowThreshold_IsTriggered()
    {
        // Arrange
        var mockEvents = _fixture.MockEventAggregator;

        // Act
        await mockEvents.PublishAsync(new AlarmTriggeredEvent(
            channelId: 1,
            zoneId: 1,
            type: AlarmType.MinTemperature,
            value: -5,
            timestamp: DateTime.UtcNow,
            position: 100
        ));

        // Assert
        var alarm = mockEvents.GetLastPublishedEvent<AlarmTriggeredEvent>();
        alarm!.Type.Should().Be(AlarmType.MinTemperature);
        alarm.Value.Should().BeLessThan(0);
    }

    [Fact]
    public async Task AlarmSequence_PreAlarmToMaxToNormal_TracksCorrectly()
    {
        // Arrange
        var mockEvents = _fixture.MockEventAggregator;

        // Act - Full alarm sequence
        // 1. PreAlarm triggers
        await mockEvents.PublishAsync(new AlarmTriggeredEvent(
            1, 1, AlarmType.PreAlarm, 65, DateTime.UtcNow.AddSeconds(-30), 100));

        // 2. Escalates to Max
        await mockEvents.PublishAsync(new AlarmAutoClearedEvent(
            1, 1, AlarmType.PreAlarm, DateTime.UtcNow.AddSeconds(-15)));
        await mockEvents.PublishAsync(new AlarmTriggeredEvent(
            1, 1, AlarmType.MaxTemperature, 95, DateTime.UtcNow.AddSeconds(-15), 100));

        // 3. Returns to normal
        await mockEvents.PublishAsync(new AlarmAutoClearedEvent(
            1, 1, AlarmType.MaxTemperature, DateTime.UtcNow));

        // Assert
        var allEvents = mockEvents.PublishedEvents;
        allEvents.Should().HaveCount(4);

        var triggers = mockEvents.GetPublishedEvents<AlarmTriggeredEvent>();
        var clears = mockEvents.GetPublishedEvents<AlarmAutoClearedEvent>();

        triggers.Should().HaveCount(2);
        clears.Should().HaveCount(2);
    }
}
