using FireRakshak.FRMC.Core.Enums;
using FireRakshak.FRMC.Core.EventAggregator;
using FireRakshak.FRMC.Core.Events;
using FireRakshak.FRMC.Core.Models;
using FireRakshak.FRMC.IntegrationTests.Fixtures;
using FireRakshak.FRMC.IntegrationTests.Mocks;
using FireRakshak.FRMC.Managers.AlarmManager;
using FireRakshak.FRMC.Managers.RelayManager;
using FireRakshak.FRMC.Managers.SystemConfigurationManager;
using FireRakshak.FRMC.Managers.SystemHealthManager;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace FireRakshak.FRMC.IntegrationTests.Tests;

/// <summary>
/// Integration tests specifically for relay auto-clear logic.
/// Tests that relays are correctly turned off when alarm conditions return to normal.
/// </summary>
public class RelayAutoClearTests
{
    private readonly ITestOutputHelper _output;
    private readonly ILogger<RelayControlManager> _logger;
    private readonly MockRS232Manager _mockRS232;
    private readonly Mock<IAlarmManager> _mockAlarmManager;
    private readonly Mock<IConfigurationManager> _mockConfigManager;
    private readonly Mock<ISystemHealthManager> _mockSystemHealthManager;
    private readonly TestEventAggregator _eventAggregator;

    public RelayAutoClearTests(ITestOutputHelper output)
    {
        _output = output;
        _logger = new TestLogger<RelayControlManager>(output);
        _mockRS232 = new MockRS232Manager();
        _mockAlarmManager = new Mock<IAlarmManager>();
        _mockConfigManager = new Mock<IConfigurationManager>();
        _mockSystemHealthManager = new Mock<ISystemHealthManager>();
        _eventAggregator = new TestEventAggregator();
    }

    private RelayControlManager CreateRelayControlManager()
    {
        return new RelayControlManager(
            _eventAggregator,
            _logger,
            _mockConfigManager.Object,
            _mockAlarmManager.Object,
            _mockRS232,
            _mockSystemHealthManager.Object);
    }

    [Fact]
    public async Task AlarmAutoClear_SingleZone_RelayTurnsOff()
    {
        // Arrange
        _mockRS232.StartAsync().Wait();

        var zoneConfig = new ZoneConfiguration
        {
            ChannelId = 1,
            ZoneId = 1,
            AssignedRelayId = 12,
            Enabled = true
        };

        _mockConfigManager.Setup(m => m.GetZoneConfigurations())
            .Returns(new List<ZoneConfiguration> { zoneConfig });

        _mockConfigManager.Setup(m => m.GetZoneConfigAsync(1, 1))
            .ReturnsAsync(zoneConfig);

        _mockConfigManager.Setup(m => m.GetSystemConfigurationAsync())
            .ReturnsAsync(new SystemConfiguration { NumberOfRelays = 48 });

        // No active alarms after this one clears
        _mockAlarmManager.Setup(m => m.GetActiveAlarmsAsync())
            .ReturnsAsync(new List<ActiveAlarm>());

        var manager = CreateRelayControlManager();
        manager.InitializeRelays(48);

        // Simulate alarm trigger - turn relay ON
        await _eventAggregator.PublishAsync(new AlarmTriggeredEvent(
            channelId: 1,
            zoneId: 1,
            type: AlarmType.MaxTemperature,
            value: 95,
            timestamp: DateTime.UtcNow,
            position: 100
        ));

        // Wait for event processing
        await Task.Delay(100);

        // Verify relay is ON
        var relayState = await manager.GetRelayStateAsync(12);
        relayState.Should().NotBeNull();
        relayState!.IsOn.Should().BeTrue("Relay should be ON after alarm triggered");
        _output.WriteLine($"Relay 12 state after trigger: IsOn={relayState.IsOn}");

        // Act - Alarm condition returns to normal
        await _eventAggregator.PublishAsync(new AlarmAutoClearedEvent(
            channelId: 1,
            zoneId: 1,
            type: AlarmType.MaxTemperature,
            timestamp: DateTime.UtcNow
        ));

        // Wait for event processing
        await Task.Delay(100);

        // Assert
        relayState = await manager.GetRelayStateAsync(12);
        relayState.Should().NotBeNull();
        relayState!.IsOn.Should().BeFalse("Relay should be OFF after alarm auto-cleared");
        _output.WriteLine($"Relay 12 state after auto-clear: IsOn={relayState.IsOn}");

        // Verify RS232 commands were sent
        var relayCommands = _mockRS232.GetRelayCommandsForRelay(12);
        relayCommands.Should().HaveCount(2, "Should have ON and OFF commands");
        relayCommands.First().TurnOn.Should().BeTrue();
        relayCommands.Last().TurnOn.Should().BeFalse();
    }

    [Fact]
    public async Task AlarmAutoClear_MultipleZonesSameRelay_RelayStaysOnUntilAllClear()
    {
        // Arrange
        _mockRS232.StartAsync().Wait();

        var zone1Config = new ZoneConfiguration { ChannelId = 1, ZoneId = 1, AssignedRelayId = 12, Enabled = true };
        var zone2Config = new ZoneConfiguration { ChannelId = 1, ZoneId = 2, AssignedRelayId = 12, Enabled = true };

        _mockConfigManager.Setup(m => m.GetZoneConfigurations())
            .Returns(new List<ZoneConfiguration> { zone1Config, zone2Config });

        _mockConfigManager.Setup(m => m.GetZoneConfigAsync(1, 1)).ReturnsAsync(zone1Config);
        _mockConfigManager.Setup(m => m.GetZoneConfigAsync(1, 2)).ReturnsAsync(zone2Config);
        _mockConfigManager.Setup(m => m.GetSystemConfigurationAsync())
            .ReturnsAsync(new SystemConfiguration { NumberOfRelays = 48 });

        var manager = CreateRelayControlManager();
        manager.InitializeRelays(48);

        // Trigger alarms on both zones
        await _eventAggregator.PublishAsync(new AlarmTriggeredEvent(1, 1, AlarmType.MaxTemperature, 95, DateTime.UtcNow, 100));
        await _eventAggregator.PublishAsync(new AlarmTriggeredEvent(1, 2, AlarmType.MaxTemperature, 92, DateTime.UtcNow, 300));
        await Task.Delay(100);

        // Zone 2 still has alarm when zone 1 clears
        _mockAlarmManager.Setup(m => m.GetActiveAlarmsAsync())
            .ReturnsAsync(new List<ActiveAlarm>
            {
                new ActiveAlarm(2, 1, 2, AlarmType.MaxTemperature, 92, DateTime.UtcNow, new ZoneState(2, "Z2", 200, 400, 85, 92, 78, false, true, false, DateTime.UtcNow))
            });

        // Act - Clear zone 1 alarm
        await _eventAggregator.PublishAsync(new AlarmAutoClearedEvent(1, 1, AlarmType.MaxTemperature, DateTime.UtcNow));
        await Task.Delay(100);

        // Assert - Relay should still be ON (zone 2 still has alarm)
        var relayState = await manager.GetRelayStateAsync(12);
        relayState!.IsOn.Should().BeTrue("Relay should stay ON because zone 2 still has alarm");
        _output.WriteLine($"After zone 1 clear: Relay 12 IsOn={relayState.IsOn}");

        // Now clear zone 2 (no more alarms)
        _mockAlarmManager.Setup(m => m.GetActiveAlarmsAsync())
            .ReturnsAsync(new List<ActiveAlarm>());

        await _eventAggregator.PublishAsync(new AlarmAutoClearedEvent(1, 2, AlarmType.MaxTemperature, DateTime.UtcNow));
        await Task.Delay(100);

        // Assert - Relay should now be OFF
        relayState = await manager.GetRelayStateAsync(12);
        relayState!.IsOn.Should().BeFalse("Relay should be OFF after all alarms cleared");
        _output.WriteLine($"After zone 2 clear: Relay 12 IsOn={relayState.IsOn}");
    }

    [Fact]
    public async Task AlarmAutoClear_MultipleAlarmTypesSameZone_RelayStaysOnUntilAllTypesCleared()
    {
        // This test verifies that when a zone has multiple alarm types active (e.g., MaxTemp AND PreAlarm),
        // clearing one alarm type does NOT turn off the relay - it stays ON until ALL alarm types clear.
        // Arrange
        _mockRS232.StartAsync().Wait();

        var zoneConfig = new ZoneConfiguration { ChannelId = 1, ZoneId = 1, AssignedRelayId = 12, Enabled = true };

        _mockConfigManager.Setup(m => m.GetZoneConfigurations())
            .Returns(new List<ZoneConfiguration> { zoneConfig });
        _mockConfigManager.Setup(m => m.GetZoneConfigAsync(1, 1)).ReturnsAsync(zoneConfig);
        _mockConfigManager.Setup(m => m.GetSystemConfigurationAsync())
            .ReturnsAsync(new SystemConfiguration { NumberOfRelays = 48 });

        var manager = CreateRelayControlManager();
        manager.InitializeRelays(48);

        // Trigger TWO different alarm types on the SAME zone
        await _eventAggregator.PublishAsync(new AlarmTriggeredEvent(1, 1, AlarmType.MaxTemperature, 95, DateTime.UtcNow, 100));
        await _eventAggregator.PublishAsync(new AlarmTriggeredEvent(1, 1, AlarmType.PreAlarm, 72, DateTime.UtcNow, 100));
        await Task.Delay(100);

        var relayState = await manager.GetRelayStateAsync(12);
        relayState!.IsOn.Should().BeTrue("Relay should be ON with multiple alarm types");
        _output.WriteLine($"After both alarms triggered: Relay 12 IsOn={relayState.IsOn}");

        // Setup mock: PreAlarm is still active after MaxTemperature clears
        _mockAlarmManager.Setup(m => m.GetActiveAlarmsAsync())
            .ReturnsAsync(new List<ActiveAlarm>
            {
                new ActiveAlarm(
                    alarmId: 2,
                    channelId: 1,
                    zoneId: 1,
                    type: AlarmType.PreAlarm,
                    triggerValue: 72,
                    triggeredAt: DateTime.UtcNow,
                    zoneStateSnapshot: new ZoneState(1, "Zone1", 0, 500, 65, 72, 60, false, false, true, DateTime.UtcNow))
            });

        // Act - Clear ONLY MaxTemperature alarm (PreAlarm still active)
        await _eventAggregator.PublishAsync(new AlarmAutoClearedEvent(1, 1, AlarmType.MaxTemperature, DateTime.UtcNow));
        await Task.Delay(100);

        // Assert - Relay should STILL be ON because PreAlarm is still active
        relayState = await manager.GetRelayStateAsync(12);
        relayState!.IsOn.Should().BeTrue("Relay should stay ON because PreAlarm is still active for the zone");
        _output.WriteLine($"After MaxTemp clear (PreAlarm still active): Relay 12 IsOn={relayState.IsOn}");

        // Setup mock: No alarms remain after PreAlarm clears
        _mockAlarmManager.Setup(m => m.GetActiveAlarmsAsync())
            .ReturnsAsync(new List<ActiveAlarm>());

        // Act - Clear PreAlarm (now ALL alarms are cleared)
        await _eventAggregator.PublishAsync(new AlarmAutoClearedEvent(1, 1, AlarmType.PreAlarm, DateTime.UtcNow));
        await Task.Delay(100);

        // Assert - Relay should now be OFF
        relayState = await manager.GetRelayStateAsync(12);
        relayState!.IsOn.Should().BeFalse("Relay should be OFF after ALL alarm types cleared");
        _output.WriteLine($"After PreAlarm clear (all alarms cleared): Relay 12 IsOn={relayState.IsOn}");
    }

    [Fact]
    public async Task AlarmAutoClear_OrphanRelay_TurnsOff()
    {
        // This tests the bug fix: relay is ON but zone config changed (no zones assigned)
        // Arrange
        _mockRS232.StartAsync().Wait();

        var zoneConfig = new ZoneConfiguration { ChannelId = 1, ZoneId = 1, AssignedRelayId = 12, Enabled = true };

        _mockConfigManager.Setup(m => m.GetZoneConfigurations())
            .Returns(new List<ZoneConfiguration> { zoneConfig });
        _mockConfigManager.Setup(m => m.GetZoneConfigAsync(1, 1)).ReturnsAsync(zoneConfig);
        _mockConfigManager.Setup(m => m.GetSystemConfigurationAsync())
            .ReturnsAsync(new SystemConfiguration { NumberOfRelays = 48 });
        _mockAlarmManager.Setup(m => m.GetActiveAlarmsAsync())
            .ReturnsAsync(new List<ActiveAlarm>());

        var manager = CreateRelayControlManager();
        manager.InitializeRelays(48);

        // Trigger alarm - relay turns ON
        await _eventAggregator.PublishAsync(new AlarmTriggeredEvent(1, 1, AlarmType.MaxTemperature, 95, DateTime.UtcNow, 100));
        await Task.Delay(100);

        var relayState = await manager.GetRelayStateAsync(12);
        relayState!.IsOn.Should().BeTrue();
        _output.WriteLine($"Relay 12 is ON after alarm trigger");

        // Simulate zone config change - relay unassigned
        _mockConfigManager.Setup(m => m.GetZoneConfigurations())
            .Returns(new List<ZoneConfiguration>
            {
                new ZoneConfiguration { ChannelId = 1, ZoneId = 1, AssignedRelayId = null, Enabled = true }
            });

        // Act - Alarm auto-clears
        await _eventAggregator.PublishAsync(new AlarmAutoClearedEvent(1, 1, AlarmType.MaxTemperature, DateTime.UtcNow));
        await Task.Delay(100);

        // Assert - Relay should be OFF even though no zones are assigned (orphan relay fix)
        relayState = await manager.GetRelayStateAsync(12);
        relayState!.IsOn.Should().BeFalse("Orphan relay should be turned OFF");
        _output.WriteLine($"Relay 12 IsOn={relayState.IsOn} after orphan relay fix");
    }

    [Fact]
    public async Task TemperatureRiseAndFall_RelayFollowsAlarmState()
    {
        // This test simulates the realistic scenario:
        // 1. Temperature rises above threshold -> alarm triggers -> relay ON
        // 2. Temperature falls below threshold -> alarm auto-clears -> relay OFF

        // Arrange
        await _mockRS232.StartAsync();

        var zoneConfig = new ZoneConfiguration
        {
            ChannelId = 1,
            ZoneId = 1,
            AssignedRelayId = 12,
            Enabled = true,
            MaxTemp = 80.0,  // Threshold
            IsMaxTempEnabled = true
        };

        _mockConfigManager.Setup(m => m.GetZoneConfigurations())
            .Returns(new List<ZoneConfiguration> { zoneConfig });
        _mockConfigManager.Setup(m => m.GetZoneConfigAsync(1, 1)).ReturnsAsync(zoneConfig);
        _mockConfigManager.Setup(m => m.GetSystemConfigurationAsync())
            .ReturnsAsync(new SystemConfiguration { NumberOfRelays = 48 });

        var manager = CreateRelayControlManager();
        manager.InitializeRelays(48);

        _output.WriteLine("=== SCENARIO: Temperature Rise and Fall ===");
        _output.WriteLine($"Zone 1 MaxTemp Threshold: {zoneConfig.MaxTemp}°C");
        _output.WriteLine($"Assigned Relay: {zoneConfig.AssignedRelayId}");
        _output.WriteLine("");

        // Initial state - no alarms
        _mockAlarmManager.Setup(m => m.GetActiveAlarmsAsync())
            .ReturnsAsync(new List<ActiveAlarm>());

        var relayState = await manager.GetRelayStateAsync(12);
        relayState!.IsOn.Should().BeFalse("Relay should be OFF initially");
        _output.WriteLine("INITIAL STATE: Relay 12 is OFF");

        // ========================================
        // STEP 1: Temperature rises to 95°C (above 80°C threshold)
        // ========================================
        _output.WriteLine("");
        _output.WriteLine("--- STEP 1: Temperature rises to 95°C (above 80°C threshold) ---");

        // Simulate alarm being added to active alarms
        var activeAlarm = new ActiveAlarm(
            alarmId: 1,
            channelId: 1,
            zoneId: 1,
            type: AlarmType.MaxTemperature,
            triggerValue: 95.0,
            triggeredAt: DateTime.UtcNow,
            zoneStateSnapshot: new ZoneState(1, "Zone 1", 0, 200, 90, 95, 85, false, true, false, DateTime.UtcNow)
        );

        _mockAlarmManager.Setup(m => m.GetActiveAlarmsAsync())
            .ReturnsAsync(new List<ActiveAlarm> { activeAlarm });

        // Publish alarm triggered event (simulating what ChannelsManager does when threshold breached)
        await _eventAggregator.PublishAsync(new AlarmTriggeredEvent(
            channelId: 1,
            zoneId: 1,
            type: AlarmType.MaxTemperature,
            value: 95.0,  // Current temperature
            timestamp: DateTime.UtcNow,
            position: 150.0
        ));

        await Task.Delay(100);

        relayState = await manager.GetRelayStateAsync(12);
        relayState!.IsOn.Should().BeTrue("Relay should be ON when temperature exceeds threshold");
        _output.WriteLine($"Temperature: 95°C -> Alarm TRIGGERED -> Relay 12 is {(relayState.IsOn ? "ON" : "OFF")}");
        _mockRS232.GetCurrentAlarmState().Should().BeTrue("STM32 heartbeat should be ON");
        _output.WriteLine($"STM32 Heartbeat Alarm: {(_mockRS232.GetCurrentAlarmState() ? "ON" : "OFF")}");

        // ========================================
        // STEP 2: Temperature drops to 75°C (below 80°C threshold)
        // ========================================
        _output.WriteLine("");
        _output.WriteLine("--- STEP 2: Temperature drops to 75°C (below 80°C threshold) ---");

        // Simulate alarm being removed from active alarms (auto-cleared)
        _mockAlarmManager.Setup(m => m.GetActiveAlarmsAsync())
            .ReturnsAsync(new List<ActiveAlarm>());

        // Publish alarm auto-cleared event (simulating what ChannelsManager does when condition returns to normal)
        await _eventAggregator.PublishAsync(new AlarmAutoClearedEvent(
            channelId: 1,
            zoneId: 1,
            type: AlarmType.MaxTemperature,
            timestamp: DateTime.UtcNow
        ));

        await Task.Delay(100);

        relayState = await manager.GetRelayStateAsync(12);
        relayState!.IsOn.Should().BeFalse("Relay should be OFF when temperature returns to normal");
        _output.WriteLine($"Temperature: 75°C -> Alarm AUTO-CLEARED -> Relay 12 is {(relayState.IsOn ? "ON" : "OFF")}");
        _mockRS232.GetCurrentAlarmState().Should().BeFalse("STM32 heartbeat should be OFF");
        _output.WriteLine($"STM32 Heartbeat Alarm: {(_mockRS232.GetCurrentAlarmState() ? "ON" : "OFF")}");

        // ========================================
        // Verify RS232 command sequence
        // ========================================
        _output.WriteLine("");
        _output.WriteLine("=== RS232 Command Sequence ===");
        var commands = _mockRS232.GetRelayCommandsForRelay(12);
        commands.Should().HaveCount(2, "Should have exactly 2 commands: ON then OFF");

        for (int i = 0; i < commands.Count; i++)
        {
            _output.WriteLine($"  {i + 1}. Relay {commands[i].RelayId} -> {(commands[i].TurnOn ? "ON" : "OFF")} at {commands[i].Timestamp:HH:mm:ss.fff}");
        }

        commands[0].TurnOn.Should().BeTrue("First command should turn relay ON");
        commands[1].TurnOn.Should().BeFalse("Second command should turn relay OFF");

        _output.WriteLine("");
        _output.WriteLine("=== TEST PASSED: Relay correctly follows alarm state ===");
    }

    [Fact]
    public async Task TemperatureOscillation_RelayTogglesCorrectly()
    {
        // Test multiple temperature oscillations around threshold
        await _mockRS232.StartAsync();

        var zoneConfig = new ZoneConfiguration
        {
            ChannelId = 1,
            ZoneId = 1,
            AssignedRelayId = 12,
            Enabled = true,
            MaxTemp = 80.0,
            IsMaxTempEnabled = true
        };

        _mockConfigManager.Setup(m => m.GetZoneConfigurations())
            .Returns(new List<ZoneConfiguration> { zoneConfig });
        _mockConfigManager.Setup(m => m.GetZoneConfigAsync(1, 1)).ReturnsAsync(zoneConfig);
        _mockConfigManager.Setup(m => m.GetSystemConfigurationAsync())
            .ReturnsAsync(new SystemConfiguration { NumberOfRelays = 48 });

        var manager = CreateRelayControlManager();
        manager.InitializeRelays(48);

        _output.WriteLine("=== SCENARIO: Temperature Oscillation (3 cycles) ===");
        _output.WriteLine($"Threshold: 80°C");
        _output.WriteLine("");

        // Simulate 3 cycles of temperature rise and fall
        var temperatures = new[] { 85.0, 75.0, 90.0, 70.0, 82.0, 78.0 };
        var expectedRelayStates = new[] { true, false, true, false, true, false };

        for (int i = 0; i < temperatures.Length; i++)
        {
            var temp = temperatures[i];
            var isAboveThreshold = temp > zoneConfig.MaxTemp;

            _output.WriteLine($"Cycle {i / 2 + 1}.{(i % 2 == 0 ? "a" : "b")}: Temperature = {temp}°C ({(isAboveThreshold ? "ABOVE" : "BELOW")} threshold)");

            if (isAboveThreshold)
            {
                // Temperature above threshold - alarm triggers
                _mockAlarmManager.Setup(m => m.GetActiveAlarmsAsync())
                    .ReturnsAsync(new List<ActiveAlarm>
                    {
                        new ActiveAlarm(i + 1, 1, 1, AlarmType.MaxTemperature, temp, DateTime.UtcNow,
                            new ZoneState(1, "Zone 1", 0, 200, temp - 5, temp, temp - 10, false, true, false, DateTime.UtcNow))
                    });

                await _eventAggregator.PublishAsync(new AlarmTriggeredEvent(1, 1, AlarmType.MaxTemperature, temp, DateTime.UtcNow, 150));
            }
            else
            {
                // Temperature below threshold - alarm clears
                _mockAlarmManager.Setup(m => m.GetActiveAlarmsAsync())
                    .ReturnsAsync(new List<ActiveAlarm>());

                await _eventAggregator.PublishAsync(new AlarmAutoClearedEvent(1, 1, AlarmType.MaxTemperature, DateTime.UtcNow));
            }

            await Task.Delay(50);

            var relayState = await manager.GetRelayStateAsync(12);
            relayState!.IsOn.Should().Be(expectedRelayStates[i], $"Relay should be {(expectedRelayStates[i] ? "ON" : "OFF")} at {temp}°C");
            _output.WriteLine($"  -> Relay 12: {(relayState.IsOn ? "ON" : "OFF")} ✓");
        }

        _output.WriteLine("");
        _output.WriteLine($"Total RS232 commands sent: {_mockRS232.RelayCommands.Count}");
        _output.WriteLine("=== TEST PASSED: Relay toggles correctly through oscillations ===");
    }

    [Fact]
    public async Task AlarmAutoClear_STM32Heartbeat_TurnsOffWhenAllClear()
    {
        // Arrange
        _mockRS232.StartAsync().Wait();

        var zoneConfig = new ZoneConfiguration { ChannelId = 1, ZoneId = 1, AssignedRelayId = 12, Enabled = true };

        _mockConfigManager.Setup(m => m.GetZoneConfigurations())
            .Returns(new List<ZoneConfiguration> { zoneConfig });
        _mockConfigManager.Setup(m => m.GetZoneConfigAsync(1, 1)).ReturnsAsync(zoneConfig);
        _mockConfigManager.Setup(m => m.GetSystemConfigurationAsync())
            .ReturnsAsync(new SystemConfiguration { NumberOfRelays = 48 });
        _mockAlarmManager.Setup(m => m.GetActiveAlarmsAsync())
            .ReturnsAsync(new List<ActiveAlarm>());

        var manager = CreateRelayControlManager();
        manager.InitializeRelays(48);

        // Trigger alarm
        await _eventAggregator.PublishAsync(new AlarmTriggeredEvent(1, 1, AlarmType.MaxTemperature, 95, DateTime.UtcNow, 100));
        await Task.Delay(100);

        // Verify heartbeat alarm is ON
        _mockRS232.GetCurrentAlarmState().Should().BeTrue("Heartbeat should be ON during alarm");

        // Act - Alarm auto-clears
        await _eventAggregator.PublishAsync(new AlarmAutoClearedEvent(1, 1, AlarmType.MaxTemperature, DateTime.UtcNow));
        await Task.Delay(100);

        // Assert
        _mockRS232.GetCurrentAlarmState().Should().BeFalse("Heartbeat should be OFF when all alarms cleared");
        _output.WriteLine($"STM32 Heartbeat alarm state: {_mockRS232.GetCurrentAlarmState()}");
    }
}

/// <summary>
/// Test logger that outputs to xUnit test output
/// </summary>
public class TestLogger<T> : ILogger<T>
{
    private readonly ITestOutputHelper _output;

    public TestLogger(ITestOutputHelper output)
    {
        _output = output;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        _output.WriteLine($"[{logLevel}] {formatter(state, exception)}");
    }
}

/// <summary>
/// Test event aggregator that actually invokes handlers
/// </summary>
public class TestEventAggregator : IEventAggregator
{
    private readonly Dictionary<Type, List<Func<IEvent, Task>>> _handlers = new();

    public async Task PublishAsync<TEvent>(TEvent @event) where TEvent : IEvent
    {
        if (_handlers.TryGetValue(typeof(TEvent), out var handlers))
        {
            foreach (var handler in handlers)
            {
                await handler(@event);
            }
        }
    }

    public IDisposable Subscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : IEvent
    {
        var eventType = typeof(TEvent);
        if (!_handlers.ContainsKey(eventType))
        {
            _handlers[eventType] = new List<Func<IEvent, Task>>();
        }

        _handlers[eventType].Add(e => handler((TEvent)e));
        return new TestSubscription();
    }

    public void Unsubscribe(IDisposable subscription)
    {
        // No-op for tests
    }

    private class TestSubscription : IDisposable
    {
        public void Dispose() { }
    }
}
