using FireRakshak.FRMC.Core.Enums;
using FireRakshak.FRMC.Core.Events;
using FireRakshak.FRMC.Core.Models;
using FluentAssertions;
using Xunit;

namespace FireRakshak.FRMC.IntegrationTests.SystemTests;

/// <summary>
/// System integration tests using the REAL EventAggregator.
/// Tests event-driven workflows as they would occur in production.
/// </summary>
[Collection("System Integration Tests")]
public class EventDrivenSystemTests
{
    private readonly SystemTestFixture _fixture;

    public EventDrivenSystemTests(SystemTestFixture fixture)
    {
        _fixture = fixture;
        _fixture.Reset();
    }

    [Fact]
    public async Task RealEventAggregator_PublishAndSubscribe_WorksCorrectly()
    {
        // Arrange
        var eventAggregator = _fixture.EventAggregator;
        TemperatureDataReceivedEvent? receivedEvent = null;
        var receivedTcs = new TaskCompletionSource<bool>();

        var subscription = eventAggregator.Subscribe<TemperatureDataReceivedEvent>(e =>
        {
            receivedEvent = e;
            receivedTcs.TrySetResult(true);
            return Task.CompletedTask;
        });

        // Act
        var testData = new[] { new TemperatureDataPoint(25.5, 100) };
        await eventAggregator.PublishAsync(new TemperatureDataReceivedEvent(
            channelId: 1,
            data: testData,
            timestamp: DateTime.UtcNow));

        // Wait for event with timeout
        await Task.WhenAny(receivedTcs.Task, Task.Delay(1000));

        // Assert
        receivedEvent.Should().NotBeNull();
        receivedEvent!.ChannelId.Should().Be(1);
        receivedEvent.Data.Should().HaveCount(1);
        receivedEvent.Data[0].Temperature.Should().Be(25.5);

        eventAggregator.Unsubscribe(subscription);
    }

    [Fact]
    public async Task RealEventAggregator_MultipleSubscribers_AllReceiveEvent()
    {
        // Arrange
        var eventAggregator = _fixture.EventAggregator;
        var receivedCount = 0;
        var subscriptions = new List<IDisposable>();
        var allReceivedTcs = new TaskCompletionSource<bool>();

        for (int i = 0; i < 3; i++)
        {
            var sub = eventAggregator.Subscribe<AlarmTriggeredEvent>(e =>
            {
                Interlocked.Increment(ref receivedCount);
                if (receivedCount >= 3)
                    allReceivedTcs.TrySetResult(true);
                return Task.CompletedTask;
            });
            subscriptions.Add(sub);
        }

        // Act
        await eventAggregator.PublishAsync(new AlarmTriggeredEvent(
            1, 1, AlarmType.MaxTemperature, 95, DateTime.UtcNow, 100));

        await Task.WhenAny(allReceivedTcs.Task, Task.Delay(1000));

        // Assert
        receivedCount.Should().Be(3);

        // Cleanup
        foreach (var sub in subscriptions)
            eventAggregator.Unsubscribe(sub);
    }

    [Fact]
    public async Task TemperatureDataFlow_DtsToEvent_CompletePipeline()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        var eventAggregator = _fixture.EventAggregator;

        await mockDts.ConnectAsync();
        mockDts.BaseTemperature = 30;

        var receivedEvents = new List<TemperatureDataReceivedEvent>();
        var subscription = eventAggregator.Subscribe<TemperatureDataReceivedEvent>(e =>
        {
            receivedEvents.Add(e);
            return Task.CompletedTask;
        });

        // Act - Simulate DTS reading and publishing (as background service would do)
        for (byte channel = 1; channel <= 2; channel++)
        {
            var data = await mockDts.ReadChannelTemperatureAsync(channel);

            await eventAggregator.PublishAsync(new TemperatureDataReceivedEvent(
                channelId: channel,
                data: data,
                timestamp: DateTime.UtcNow));
        }

        await Task.Delay(100); // Allow events to propagate

        // Assert
        receivedEvents.Should().HaveCount(2);
        receivedEvents.Should().Contain(e => e.ChannelId == 1);
        receivedEvents.Should().Contain(e => e.ChannelId == 2);
        receivedEvents.All(e => e.Data.Length > 0).Should().BeTrue();

        eventAggregator.Unsubscribe(subscription);
    }

    [Fact]
    public async Task AlarmTriggeredToRelayCycle_FullEventFlow()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        var eventAggregator = _fixture.EventAggregator;
        var zone = _fixture.GetZoneConfig(1, 1);

        await mockRS232.StartAsync();

        RelayStateChangedEvent? relayEvent = null;
        var subscription = eventAggregator.Subscribe<RelayStateChangedEvent>(e =>
        {
            relayEvent = e;
            return Task.CompletedTask;
        });

        // Act - Simulate alarm trigger → relay activation → relay state change event
        // 1. Alarm triggered
        await eventAggregator.PublishAsync(new AlarmTriggeredEvent(
            zone.ChannelId, zone.ZoneId, AlarmType.MaxTemperature, 95, DateTime.UtcNow, 100));

        // 2. Relay activated (as RelayControlManager would do)
        await mockRS232.SendRelayCommandAsync((byte)zone.AssignedRelayId!.Value, true);

        // 3. Relay state change event published
        await eventAggregator.PublishAsync(new RelayStateChangedEvent(
            relayId: zone.AssignedRelayId.Value,
            newState: true,
            source: RelayControlSource.Auto,
            changedBy: "AlarmManager",
            timestamp: DateTime.UtcNow));

        await Task.Delay(100);

        // Assert
        relayEvent.Should().NotBeNull();
        relayEvent!.RelayId.Should().Be(zone.AssignedRelayId.Value);
        relayEvent.NewState.Should().BeTrue();
        relayEvent.Source.Should().Be(RelayControlSource.Auto);

        mockRS232.GetLastRelayCommand()!.TurnOn.Should().BeTrue();

        eventAggregator.Unsubscribe(subscription);
    }

    [Fact]
    public async Task AlarmClearCycle_FullEventFlow()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        var eventAggregator = _fixture.EventAggregator;
        var zone = _fixture.GetZoneConfig(1, 1);

        await mockRS232.StartAsync();

        var clearedEvents = new List<AlarmAutoClearedEvent>();
        var subscription = eventAggregator.Subscribe<AlarmAutoClearedEvent>(e =>
        {
            clearedEvents.Add(e);
            return Task.CompletedTask;
        });

        // Act
        // 1. Initial alarm
        await mockRS232.SendRelayCommandAsync((byte)zone.AssignedRelayId!.Value, true);
        await mockRS232.SetHeartbeatAlarmAsync(true);

        // 2. Temperature normalizes, alarm auto-clears
        await eventAggregator.PublishAsync(new AlarmAutoClearedEvent(
            zone.ChannelId, zone.ZoneId, AlarmType.MaxTemperature, DateTime.UtcNow));

        // 3. Relay deactivated
        await mockRS232.SendRelayCommandAsync((byte)zone.AssignedRelayId!.Value, false);
        await mockRS232.SetHeartbeatAlarmAsync(false);

        await Task.Delay(100);

        // Assert
        clearedEvents.Should().HaveCount(1);
        clearedEvents[0].ChannelId.Should().Be(zone.ChannelId);
        clearedEvents[0].ZoneId.Should().Be(zone.ZoneId);

        mockRS232.GetLastRelayCommand()!.TurnOn.Should().BeFalse();
        mockRS232.GetCurrentAlarmState().Should().BeFalse();

        eventAggregator.Unsubscribe(subscription);
    }

    [Fact]
    public async Task FiberBreakDetection_FullEventFlow()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        var eventAggregator = _fixture.EventAggregator;

        await mockDts.ConnectAsync();
        mockDts.SimulateFiberBreak = true;
        mockDts.FiberBreakPosition = 300;

        FiberBreakDetectedEvent? breakEvent = null;
        var subscription = eventAggregator.Subscribe<FiberBreakDetectedEvent>(e =>
        {
            breakEvent = e;
            return Task.CompletedTask;
        });

        // Act - Read temperature and detect fiber break
        var data = await mockDts.ReadChannelTemperatureAsync(1);

        // System would detect -999 values and publish fiber break
        var invalidDataAfterBreak = data.Count(p => p.Position > 300 && p.Temperature == -999);
        if (invalidDataAfterBreak > 0)
        {
            await eventAggregator.PublishAsync(new FiberBreakDetectedEvent(
                channelId: 1,
                position: 300,
                isBroken: true,
                timestamp: DateTime.UtcNow));
        }

        await Task.Delay(100);

        // Assert
        breakEvent.Should().NotBeNull();
        breakEvent!.ChannelId.Should().Be(1);
        breakEvent.Position.Should().Be(300);
        breakEvent.IsBroken.Should().BeTrue();

        eventAggregator.Unsubscribe(subscription);
    }

    [Fact]
    public async Task ResetButtonPress_ClearsAllAlarms()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        var eventAggregator = _fixture.EventAggregator;

        await mockRS232.StartAsync();

        // Setup multiple active alarms
        await mockRS232.SendRelayCommandAsync(11, true);
        await mockRS232.SendRelayCommandAsync(12, true);
        await mockRS232.SendRelayCommandAsync(13, true);
        await mockRS232.SetHeartbeatAlarmAsync(true);

        AlarmResetRequestedEvent? resetEvent = null;
        var subscription = eventAggregator.Subscribe<AlarmResetRequestedEvent>(e =>
        {
            resetEvent = e;
            return Task.CompletedTask;
        });

        // Act - Reset button pressed on STM32
        await eventAggregator.PublishAsync(new AlarmResetRequestedEvent(
            source: "STM32",
            timestamp: DateTime.UtcNow));

        // System clears all relays
        await mockRS232.SendRelayCommandAsync(11, false);
        await mockRS232.SendRelayCommandAsync(12, false);
        await mockRS232.SendRelayCommandAsync(13, false);
        await mockRS232.SetHeartbeatAlarmAsync(false);

        await Task.Delay(100);

        // Assert
        resetEvent.Should().NotBeNull();
        resetEvent!.Source.Should().Be("STM32");

        mockRS232.GetRelayCommandsForRelay(11).Last().TurnOn.Should().BeFalse();
        mockRS232.GetRelayCommandsForRelay(12).Last().TurnOn.Should().BeFalse();
        mockRS232.GetRelayCommandsForRelay(13).Last().TurnOn.Should().BeFalse();
        mockRS232.GetCurrentAlarmState().Should().BeFalse();

        eventAggregator.Unsubscribe(subscription);
    }

    [Fact]
    public async Task HealthParameterChange_FullEventFlow()
    {
        // Arrange
        var mockDts = _fixture.MockDts;
        var eventAggregator = _fixture.EventAggregator;

        await mockDts.ConnectAsync();

        HealthParameterChangedEvent? healthEvent = null;
        var subscription = eventAggregator.Subscribe<HealthParameterChangedEvent>(e =>
        {
            healthEvent = e;
            return Task.CompletedTask;
        });

        // Act - Read health and publish change
        var health = await mockDts.ReadHealthParametersAsync();

        await eventAggregator.PublishAsync(new HealthParameterChangedEvent(
            parameter: HealthParameter.DtsResponsive,
            value: health.DtsResponsive,
            timestamp: DateTime.UtcNow));

        await Task.Delay(100);

        // Assert
        healthEvent.Should().NotBeNull();
        healthEvent!.Parameter.Should().Be(HealthParameter.DtsResponsive);
        healthEvent.Value.Should().Be(true);

        eventAggregator.Unsubscribe(subscription);
    }

    [Fact]
    public async Task ConfigurationChange_FullEventFlow()
    {
        // Arrange
        var eventAggregator = _fixture.EventAggregator;

        ConfigurationChangedEvent? configEvent = null;
        var subscription = eventAggregator.Subscribe<ConfigurationChangedEvent>(e =>
        {
            configEvent = e;
            return Task.CompletedTask;
        });

        // Act - Configuration change
        await eventAggregator.PublishAsync(new ConfigurationChangedEvent(
            configType: ConfigurationType.Zone,
            changedBy: "Admin",
            source: "DTSCM",
            changes: new Dictionary<string, object>
            {
                { "ChannelId", 1 },
                { "ZoneId", 1 },
                { "MaxTemp", 85 }
            }));

        await Task.Delay(100);

        // Assert
        configEvent.Should().NotBeNull();
        configEvent!.ConfigType.Should().Be(ConfigurationType.Zone);
        configEvent.ChangedBy.Should().Be("Admin");
        configEvent.Source.Should().Be("DTSCM");

        eventAggregator.Unsubscribe(subscription);
    }

    [Fact]
    public async Task ConcurrentEvents_AllProcessed()
    {
        // Arrange
        var eventAggregator = _fixture.EventAggregator;
        var receivedCount = 0;
        var expectedCount = 20;
        var allReceivedTcs = new TaskCompletionSource<bool>();

        var subscription = eventAggregator.Subscribe<AlarmTriggeredEvent>(e =>
        {
            var count = Interlocked.Increment(ref receivedCount);
            if (count >= expectedCount)
                allReceivedTcs.TrySetResult(true);
            return Task.CompletedTask;
        });

        // Act - Publish many events concurrently
        var tasks = Enumerable.Range(1, expectedCount).Select(i =>
            eventAggregator.PublishAsync(new AlarmTriggeredEvent(
                1, i % 5 + 1, AlarmType.MaxTemperature, 90 + i, DateTime.UtcNow, i * 50)));

        await Task.WhenAll(tasks);
        await Task.WhenAny(allReceivedTcs.Task, Task.Delay(2000));

        // Assert
        receivedCount.Should().Be(expectedCount);

        eventAggregator.Unsubscribe(subscription);
    }

    [Fact]
    public async Task ZoneStateUpdate_FullEventFlow()
    {
        // Arrange
        var eventAggregator = _fixture.EventAggregator;

        ZoneStateUpdatedEvent? stateEvent = null;
        var subscription = eventAggregator.Subscribe<ZoneStateUpdatedEvent>(e =>
        {
            stateEvent = e;
            return Task.CompletedTask;
        });

        var zoneState = new ZoneState(
            zoneId: 1,
            name: "Zone 1.1",
            startPoint: 0,
            endPoint: 200,
            averageTemperature: 75,
            maxTemperature: 85,
            minTemperature: 65,
            preAlarm: false,
            maxAlarm: true,
            minAlarm: false,
            lastUpdate: DateTime.UtcNow);

        // Act
        await eventAggregator.PublishAsync(new ZoneStateUpdatedEvent(
            channelId: 1,
            zoneId: 1,
            state: zoneState,
            timestamp: DateTime.UtcNow));

        await Task.Delay(100);

        // Assert
        stateEvent.Should().NotBeNull();
        stateEvent!.State.MaxAlarm.Should().BeTrue();
        stateEvent.State.MaxTemperature.Should().Be(85);
        stateEvent.State.AverageTemperature.Should().Be(75);

        eventAggregator.Unsubscribe(subscription);
    }

    [Fact]
    public async Task UnsubscribeDuringPublish_HandledGracefully()
    {
        // Arrange
        var eventAggregator = _fixture.EventAggregator;
        var receivedCount = 0;
        IDisposable? subscription = null;

        subscription = eventAggregator.Subscribe<AlarmTriggeredEvent>(e =>
        {
            Interlocked.Increment(ref receivedCount);
            // Unsubscribe during event handling
            if (subscription != null)
                eventAggregator.Unsubscribe(subscription);
            return Task.CompletedTask;
        });

        // Act
        await eventAggregator.PublishAsync(new AlarmTriggeredEvent(
            1, 1, AlarmType.MaxTemperature, 95, DateTime.UtcNow, 100));

        // Second publish should not be received
        await eventAggregator.PublishAsync(new AlarmTriggeredEvent(
            1, 2, AlarmType.MaxTemperature, 95, DateTime.UtcNow, 100));

        await Task.Delay(100);

        // Assert
        receivedCount.Should().Be(1);
    }
}
