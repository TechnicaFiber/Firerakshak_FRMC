using FireRakshak.FRMC.Core.Enums;
using FireRakshak.FRMC.Core.Events;
using FireRakshak.FRMC.Core.Models;
using FireRakshak.FRMC.IntegrationTests.Fixtures;
using FireRakshak.FRMC.IntegrationTests.Mocks;
using FluentAssertions;
using Xunit;

namespace FireRakshak.FRMC.IntegrationTests.Tests;

/// <summary>
/// Integration tests for the event aggregator and event-driven architecture.
/// Tests event publishing, subscription, and cross-component communication.
/// </summary>
[Collection("FRMC Integration Tests")]
public class EventAggregatorTests
{
    private readonly FrmcTestFixture _fixture;

    public EventAggregatorTests(FrmcTestFixture fixture)
    {
        _fixture = fixture;
        _fixture.Reset();
    }

    [Fact]
    public async Task PublishEvent_WhenSubscribed_HandlerReceivesEvent()
    {
        // Arrange
        var eventAggregator = _fixture.MockEventAggregator;
        TemperatureDataReceivedEvent? receivedEvent = null;

        eventAggregator.Subscribe<TemperatureDataReceivedEvent>(e =>
        {
            receivedEvent = e;
            return Task.CompletedTask;
        });

        var testEvent = new TemperatureDataReceivedEvent(
            channelId: 1,
            data: new[] { new TemperatureDataPoint(25.5, 100) },
            timestamp: DateTime.UtcNow
        );

        // Act
        await eventAggregator.PublishAsync(testEvent);

        // Assert
        receivedEvent.Should().NotBeNull();
        receivedEvent!.ChannelId.Should().Be(1);
    }

    [Fact]
    public async Task PublishEvent_WithMultipleSubscribers_AllReceiveEvent()
    {
        // Arrange
        var eventAggregator = _fixture.MockEventAggregator;
        var receivedCount = 0;

        eventAggregator.Subscribe<AlarmTriggeredEvent>(e =>
        {
            Interlocked.Increment(ref receivedCount);
            return Task.CompletedTask;
        });

        eventAggregator.Subscribe<AlarmTriggeredEvent>(e =>
        {
            Interlocked.Increment(ref receivedCount);
            return Task.CompletedTask;
        });

        var testEvent = new AlarmTriggeredEvent(
            channelId: 1,
            zoneId: 1,
            type: AlarmType.MaxTemperature,
            value: 100,
            timestamp: DateTime.UtcNow,
            position: 150
        );

        // Act
        await eventAggregator.PublishAsync(testEvent);

        // Assert
        receivedCount.Should().Be(2);
    }

    [Fact]
    public async Task Unsubscribe_WhenCalled_HandlerNoLongerReceivesEvents()
    {
        // Arrange
        var eventAggregator = _fixture.MockEventAggregator;
        var receivedCount = 0;

        var subscription = eventAggregator.Subscribe<AlarmTriggeredEvent>(e =>
        {
            Interlocked.Increment(ref receivedCount);
            return Task.CompletedTask;
        });

        var testEvent = new AlarmTriggeredEvent(
            channelId: 1,
            zoneId: 1,
            type: AlarmType.MaxTemperature,
            value: 100,
            timestamp: DateTime.UtcNow,
            position: 150
        );

        // First publish - should receive
        await eventAggregator.PublishAsync(testEvent);
        receivedCount.Should().Be(1);

        // Act - Unsubscribe
        eventAggregator.Unsubscribe(subscription);

        // Second publish - should not receive
        await eventAggregator.PublishAsync(testEvent);

        // Assert
        receivedCount.Should().Be(1, "Handler should not receive events after unsubscribe");
    }

    [Fact]
    public async Task PublishedEvents_AreTrackedInOrder()
    {
        // Arrange
        var eventAggregator = _fixture.MockEventAggregator;

        // Act
        await eventAggregator.PublishAsync(new AlarmTriggeredEvent(1, 1, AlarmType.MaxTemperature, 100, DateTime.UtcNow, 150));
        await eventAggregator.PublishAsync(new AlarmTriggeredEvent(1, 2, AlarmType.PreAlarm, 65, DateTime.UtcNow, 250));
        await eventAggregator.PublishAsync(new AlarmAutoClearedEvent(1, 1, AlarmType.MaxTemperature, DateTime.UtcNow));

        // Assert
        var alarmEvents = eventAggregator.GetPublishedEvents<AlarmTriggeredEvent>();
        alarmEvents.Should().HaveCount(2);
        alarmEvents[0].ZoneId.Should().Be(1);
        alarmEvents[1].ZoneId.Should().Be(2);

        var clearEvents = eventAggregator.GetPublishedEvents<AlarmAutoClearedEvent>();
        clearEvents.Should().HaveCount(1);
    }

    [Fact]
    public async Task AlarmTriggeredEvent_ContainsAllRequiredData()
    {
        // Arrange
        var eventAggregator = _fixture.MockEventAggregator;
        var timestamp = DateTime.UtcNow;

        var alarmEvent = new AlarmTriggeredEvent(
            channelId: 2,
            zoneId: 5,
            type: AlarmType.RateOfRise,
            value: 15.5,
            timestamp: timestamp,
            position: 450.5
        );

        // Act
        await eventAggregator.PublishAsync(alarmEvent);

        // Assert
        var published = eventAggregator.GetLastPublishedEvent<AlarmTriggeredEvent>();
        published.Should().NotBeNull();
        published!.ChannelId.Should().Be(2);
        published.ZoneId.Should().Be(5);
        published.Type.Should().Be(AlarmType.RateOfRise);
        published.Value.Should().Be(15.5);
        published.Position.Should().Be(450.5);
        published.Timestamp.Should().Be(timestamp);
    }

    [Fact]
    public async Task ZoneStateUpdatedEvent_ContainsCompleteState()
    {
        // Arrange
        var eventAggregator = _fixture.MockEventAggregator;
        var zoneState = new ZoneState(
            zoneId: 1,
            name: "Test Zone",
            startPoint: 0,
            endPoint: 200,
            averageTemperature: 30,
            maxTemperature: 45,
            minTemperature: 25,
            preAlarm: false,
            maxAlarm: true,
            minAlarm: false,
            lastUpdate: DateTime.UtcNow
        );

        var stateEvent = new ZoneStateUpdatedEvent(
            channelId: 1,
            zoneId: 1,
            state: zoneState,
            timestamp: DateTime.UtcNow
        );

        // Act
        await eventAggregator.PublishAsync(stateEvent);

        // Assert
        var published = eventAggregator.GetLastPublishedEvent<ZoneStateUpdatedEvent>();
        published.Should().NotBeNull();
        published!.State.MaxAlarm.Should().BeTrue();
        published.State.AverageTemperature.Should().Be(30);
        published.State.MaxTemperature.Should().Be(45);
    }

    [Fact]
    public async Task FiberBreakDetectedEvent_TracksBreakPosition()
    {
        // Arrange
        var eventAggregator = _fixture.MockEventAggregator;

        var fiberBreakEvent = new FiberBreakDetectedEvent(
            channelId: 1,
            position: 500.5,
            isBroken: true,
            timestamp: DateTime.UtcNow
        );

        // Act
        await eventAggregator.PublishAsync(fiberBreakEvent);

        // Assert
        var published = eventAggregator.GetLastPublishedEvent<FiberBreakDetectedEvent>();
        published.Should().NotBeNull();
        published!.ChannelId.Should().Be(1);
        published.Position.Should().Be(500.5);
        published.IsBroken.Should().BeTrue();
    }

    [Fact]
    public async Task ConfigurationChangedEvent_TracksChangedParameters()
    {
        // Arrange
        var eventAggregator = _fixture.MockEventAggregator;

        var configEvent = new ConfigurationChangedEvent(
            configType: ConfigurationType.Zone,
            changedBy: "Admin",
            source: "DTSCM",
            changes: new Dictionary<string, object>
            {
                { "ChannelId", 1 },
                { "ZoneId", 5 },
                { "MaxTemp", 85 }
            }
        );

        // Act
        await eventAggregator.PublishAsync(configEvent);

        // Assert
        var published = eventAggregator.GetLastPublishedEvent<ConfigurationChangedEvent>();
        published.Should().NotBeNull();
        published!.ConfigType.Should().Be(ConfigurationType.Zone);
        published.ChangedBy.Should().Be("Admin");
    }

    [Fact]
    public async Task AlarmResetRequestedEvent_TracksSource()
    {
        // Arrange
        var eventAggregator = _fixture.MockEventAggregator;

        var resetEvent = new AlarmResetRequestedEvent(
            source: "STM32",
            timestamp: DateTime.UtcNow
        );

        // Act
        await eventAggregator.PublishAsync(resetEvent);

        // Assert
        var published = eventAggregator.GetLastPublishedEvent<AlarmResetRequestedEvent>();
        published.Should().NotBeNull();
        published!.Source.Should().Be("STM32");
    }

    [Fact]
    public async Task RelayStateChangedEvent_TracksStateChange()
    {
        // Arrange
        var eventAggregator = _fixture.MockEventAggregator;

        var relayEvent = new RelayStateChangedEvent(
            relayId: 11,
            newState: true,
            source: RelayControlSource.Auto,
            changedBy: "System",
            timestamp: DateTime.UtcNow
        );

        // Act
        await eventAggregator.PublishAsync(relayEvent);

        // Assert
        var published = eventAggregator.GetLastPublishedEvent<RelayStateChangedEvent>();
        published.Should().NotBeNull();
        published!.RelayId.Should().Be(11);
        published.NewState.Should().BeTrue();
        published.Source.Should().Be(RelayControlSource.Auto);
    }

    [Fact]
    public async Task EventCount_TracksCorrectly()
    {
        // Arrange
        var eventAggregator = _fixture.MockEventAggregator;

        // Act
        for (int i = 0; i < 5; i++)
        {
            await eventAggregator.PublishAsync(new AlarmTriggeredEvent(1, i + 1, AlarmType.MaxTemperature, 100, DateTime.UtcNow, null));
        }

        for (int i = 0; i < 3; i++)
        {
            await eventAggregator.PublishAsync(new AlarmAutoClearedEvent(1, i + 1, AlarmType.MaxTemperature, DateTime.UtcNow));
        }

        // Assert
        eventAggregator.GetEventCount<AlarmTriggeredEvent>().Should().Be(5);
        eventAggregator.GetEventCount<AlarmAutoClearedEvent>().Should().Be(3);
    }

    [Fact]
    public async Task HasEventOfType_ReturnsCorrectResult()
    {
        // Arrange
        var eventAggregator = _fixture.MockEventAggregator;

        // Assert before publishing
        eventAggregator.HasEventOfType<AlarmTriggeredEvent>().Should().BeFalse();

        // Act
        await eventAggregator.PublishAsync(new AlarmTriggeredEvent(1, 1, AlarmType.MaxTemperature, 100, DateTime.UtcNow, null));

        // Assert after publishing
        eventAggregator.HasEventOfType<AlarmTriggeredEvent>().Should().BeTrue();
        eventAggregator.HasEventOfType<FiberBreakDetectedEvent>().Should().BeFalse();
    }

    [Fact]
    public void ClearEvents_RemovesAllTrackedEvents()
    {
        // Arrange
        var eventAggregator = _fixture.MockEventAggregator;

        // Add some events
        eventAggregator.PublishAsync(new AlarmTriggeredEvent(1, 1, AlarmType.MaxTemperature, 100, DateTime.UtcNow, null)).Wait();
        eventAggregator.PublishAsync(new AlarmTriggeredEvent(1, 2, AlarmType.PreAlarm, 65, DateTime.UtcNow, null)).Wait();

        eventAggregator.PublishedEvents.Should().HaveCount(2);

        // Act
        eventAggregator.ClearEvents();

        // Assert
        eventAggregator.PublishedEvents.Should().BeEmpty();
    }
}
