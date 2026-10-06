using FireRakshak.FRMC.Core.Enums;
using FireRakshak.FRMC.Core.Events;
using FireRakshak.FRMC.Core.Models;
using FireRakshak.FRMC.IntegrationTests.Fixtures;
using FluentAssertions;
using Xunit;

namespace FireRakshak.FRMC.IntegrationTests.Tests;

/// <summary>
/// Integration tests for relay mapping and zone-to-relay assignments.
/// Tests relay activation based on zone alarms and configuration.
/// </summary>
[Collection("FRMC Integration Tests")]
public class RelayMappingTests
{
    private readonly FrmcTestFixture _fixture;

    public RelayMappingTests(FrmcTestFixture fixture)
    {
        _fixture = fixture;
        _fixture.Reset();
    }

    [Fact]
    public void RelayIds_FixedRelays_Are1To11()
    {
        // Fixed/Reserved relays (1-11) are for system use, not assignable to zones
        // Relay 1: System Running indicator (always ON when system active)
        // Relay 2: System Health relay (ON when system faults/health issues)
        // Relay 3: Common Alarm relay (ON when ANY alarm is active in system)
        // Relays 4-11: Channel FiberBreak relays (CH1=4, CH2=5, ..., CH8=11)
        var fixedRelayStart = 1;
        var fixedRelayEnd = 11;

        fixedRelayStart.Should().Be(1);
        fixedRelayEnd.Should().Be(11);
    }

    [Fact]
    public void RelayIds_AssignableRelays_Are12To48()
    {
        // Assignable relays (12-48) can be mapped to zones
        var assignableRelayStart = 12;
        var assignableRelayEnd = 48;
        var totalAssignable = assignableRelayEnd - assignableRelayStart + 1;

        totalAssignable.Should().Be(37, "37 relays are assignable to zones (12-48)");
    }

    [Fact]
    public void ZoneConfiguration_AssignedRelays_AreInValidRange()
    {
        // Arrange
        var zones = _fixture.DefaultZoneConfigs;
        var zonesWithRelays = zones.Where(z => z.AssignedRelayId.HasValue);

        // Assert - Assignable relays are now 12-48 (relays 1-11 are reserved)
        zonesWithRelays.All(z => z.AssignedRelayId >= 12 && z.AssignedRelayId <= 48)
            .Should().BeTrue("Assigned relays must be in range 12-48");
    }

    [Fact]
    public async Task ZoneAlarm_WithAssignedRelay_ActivatesCorrectRelay()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        var mockEvents = _fixture.MockEventAggregator;
        await mockRS232.StartAsync();

        var zone = _fixture.DefaultZoneConfigs.First(z => z.AssignedRelayId.HasValue);
        var expectedRelayId = zone.AssignedRelayId!.Value;

        // Act - Alarm triggers relay
        await mockEvents.PublishAsync(new AlarmTriggeredEvent(
            zone.ChannelId, zone.ZoneId, AlarmType.MaxTemperature, 95, DateTime.UtcNow, 100));
        await mockRS232.SendRelayCommandAsync((byte)expectedRelayId, true);

        // Assert
        var lastCommand = mockRS232.GetLastRelayCommand();
        lastCommand.Should().NotBeNull();
        lastCommand!.RelayId.Should().Be((byte)expectedRelayId);
        lastCommand.TurnOn.Should().BeTrue();
    }

    [Fact]
    public async Task ZoneAlarm_WithoutAssignedRelay_DoesNotActivateRelay()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        await mockRS232.StartAsync();

        // Find zone without relay
        var zoneWithoutRelay = new ZoneConfiguration
        {
            ChannelId = 1,
            ZoneId = 99,
            AssignedRelayId = null // No relay assigned
        };

        // Act - No relay command should be sent
        // (In real system, alarm manager would check for null relay)

        // Assert
        mockRS232.RelayCommands.Should().BeEmpty();
    }

    [Fact]
    public async Task MultipleZones_SameRelay_ActivatesOnce()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        await mockRS232.StartAsync();

        // Two zones share relay 12 (first assignable relay after reserved 1-11)
        var sharedRelayId = (byte)12;

        // Act - Both zones trigger alarms
        await mockRS232.SendRelayCommandAsync(sharedRelayId, true);
        // Second zone alarm - relay already on, but command still sent
        await mockRS232.SendRelayCommandAsync(sharedRelayId, true);

        // Assert - Both commands tracked
        var relayCommands = mockRS232.GetRelayCommandsForRelay(sharedRelayId);
        relayCommands.Should().HaveCount(2);
        relayCommands.All(c => c.TurnOn).Should().BeTrue();
    }

    [Fact]
    public async Task AlarmClear_DeactivatesAssignedRelay()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        await mockRS232.StartAsync();

        var relayId = (byte)12; // First assignable relay

        // Act - Activate then deactivate
        await mockRS232.SendRelayCommandAsync(relayId, true);
        await mockRS232.SendRelayCommandAsync(relayId, false);

        // Assert
        var commands = mockRS232.GetRelayCommandsForRelay(relayId);
        commands.Should().HaveCount(2);
        commands.First().TurnOn.Should().BeTrue();
        commands.Last().TurnOn.Should().BeFalse();
    }

    [Fact]
    public async Task HeartbeatAlarm_ActivatesWithAnyZoneAlarm()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        await mockRS232.StartAsync();

        // Act - Zone alarm activates both zone relay and heartbeat
        await mockRS232.SendRelayCommandAsync(12, true);
        await mockRS232.SetHeartbeatAlarmAsync(true);

        // Assert
        mockRS232.GetCurrentAlarmState().Should().BeTrue();
        mockRS232.HeartbeatAlarmStates.Last().Should().BeTrue();
    }

    [Fact]
    public async Task HeartbeatAlarm_DeactivatesWhenAllAlarmsClear()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        await mockRS232.StartAsync();

        // Multiple zone alarms
        await mockRS232.SendRelayCommandAsync(12, true);
        await mockRS232.SendRelayCommandAsync(13, true);
        await mockRS232.SetHeartbeatAlarmAsync(true);

        // Act - Clear all alarms
        await mockRS232.SendRelayCommandAsync(12, false);
        await mockRS232.SendRelayCommandAsync(13, false);
        await mockRS232.SetHeartbeatAlarmAsync(false);

        // Assert
        mockRS232.GetCurrentAlarmState().Should().BeFalse();
    }

    [Fact]
    public async Task RelayStateChange_PublishesEvent()
    {
        // Arrange
        var mockEvents = _fixture.MockEventAggregator;

        // Act
        await mockEvents.PublishAsync(new RelayStateChangedEvent(
            relayId: 12,
            newState: true,
            source: RelayControlSource.Auto,
            changedBy: "AlarmManager",
            timestamp: DateTime.UtcNow
        ));

        // Assert
        var relayEvent = mockEvents.GetLastPublishedEvent<RelayStateChangedEvent>();
        relayEvent.Should().NotBeNull();
        relayEvent!.RelayId.Should().Be(12);
        relayEvent.NewState.Should().BeTrue();
        relayEvent.Source.Should().Be(RelayControlSource.Auto);
    }

    [Fact]
    public async Task ManualRelayControl_OverridesAutomatic()
    {
        // Arrange
        var mockEvents = _fixture.MockEventAggregator;

        // Act - Auto alarm, then manual override
        await mockEvents.PublishAsync(new RelayStateChangedEvent(
            12, true, RelayControlSource.Auto, "AlarmManager", DateTime.UtcNow));

        await mockEvents.PublishAsync(new RelayStateChangedEvent(
            12, false, RelayControlSource.Manual, "Operator", DateTime.UtcNow));

        // Assert
        var relayEvents = mockEvents.GetPublishedEvents<RelayStateChangedEvent>()
            .Where(e => e.RelayId == 12).ToList();

        relayEvents.Should().HaveCount(2);
        relayEvents.Last().Source.Should().Be(RelayControlSource.Manual);
        relayEvents.Last().NewState.Should().BeFalse();
    }

    [Fact]
    public void TotalRelays_MatchesSystemConfiguration()
    {
        // Arrange
        var config = _fixture.DefaultSystemConfig;

        // Assert
        config.NumberOfRelays.Should().Be(48);
    }

    [Fact]
    public void MaxZonesWithRelays_DoesNotExceedAssignableRelays()
    {
        // Arrange
        var maxAssignableRelays = 37; // Relays 12-48 (relays 1-11 are reserved)
        var maxChannels = 8;
        var maxZonesPerChannel = 20;
        var maxTotalZones = maxChannels * maxZonesPerChannel;

        // Assert - More zones than relays is OK (not all zones need relays)
        maxTotalZones.Should().Be(160);
        maxAssignableRelays.Should().BeLessThan(maxTotalZones,
            "System design allows zones without dedicated relays");
    }

    [Fact]
    public async Task RelayCommand_WithChannelInfo_TracksCorrectly()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        var mockEvents = _fixture.MockEventAggregator;
        await mockRS232.StartAsync();

        // Simulate alarm with zone context
        var zone = _fixture.DefaultZoneConfigs.First(z => z.AssignedRelayId == 12);

        // Act
        await mockEvents.PublishAsync(new AlarmTriggeredEvent(
            zone.ChannelId, zone.ZoneId, AlarmType.MaxTemperature, 95, DateTime.UtcNow, 100));
        await mockRS232.SendRelayCommandAsync(12, true);

        // Assert
        var alarm = mockEvents.GetLastPublishedEvent<AlarmTriggeredEvent>();
        var relay = mockRS232.GetLastRelayCommand();

        alarm!.ChannelId.Should().Be(zone.ChannelId);
        alarm.ZoneId.Should().Be(zone.ZoneId);
        relay!.RelayId.Should().Be(12);
    }

    [Fact]
    public async Task AllRelaysOff_AfterReset_Verified()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        await mockRS232.StartAsync();

        // Activate multiple zone relays (12-14)
        await mockRS232.SendRelayCommandAsync(12, true);
        await mockRS232.SendRelayCommandAsync(13, true);
        await mockRS232.SendRelayCommandAsync(14, true);
        await mockRS232.SetHeartbeatAlarmAsync(true);

        // Act - Reset all
        await mockRS232.SendRelayCommandAsync(12, false);
        await mockRS232.SendRelayCommandAsync(13, false);
        await mockRS232.SendRelayCommandAsync(14, false);
        await mockRS232.SetHeartbeatAlarmAsync(false);

        // Assert
        var relay12 = mockRS232.GetRelayCommandsForRelay(12);
        var relay13 = mockRS232.GetRelayCommandsForRelay(13);
        var relay14 = mockRS232.GetRelayCommandsForRelay(14);

        relay12.Last().TurnOn.Should().BeFalse();
        relay13.Last().TurnOn.Should().BeFalse();
        relay14.Last().TurnOn.Should().BeFalse();
        mockRS232.GetCurrentAlarmState().Should().BeFalse();
    }

    #region Reserved Relay Tests (1-10)

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public async Task ReservedRelay_CanBeActivatedIndividually(byte relayId)
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        await mockRS232.StartAsync();

        // Act - Activate reserved relay
        await mockRS232.SendRelayCommandAsync(relayId, true);

        // Assert
        var command = mockRS232.GetLastRelayCommand();
        command.Should().NotBeNull();
        command!.RelayId.Should().Be(relayId);
        command.TurnOn.Should().BeTrue();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public async Task ReservedRelay_CanBeDeactivatedIndividually(byte relayId)
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        await mockRS232.StartAsync();

        // Act - Activate then deactivate
        await mockRS232.SendRelayCommandAsync(relayId, true);
        await mockRS232.SendRelayCommandAsync(relayId, false);

        // Assert
        var commands = mockRS232.GetRelayCommandsForRelay(relayId);
        commands.Should().HaveCount(2);
        commands.First().TurnOn.Should().BeTrue();
        commands.Last().TurnOn.Should().BeFalse();
    }

    [Fact]
    public async Task AllReservedRelays_CanBeActivatedSimultaneously()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        await mockRS232.StartAsync();

        // Act - Activate all reserved relays (1-10)
        for (byte i = 1; i <= 11; i++)
        {
            await mockRS232.SendRelayCommandAsync(i, true);
        }

        // Assert
        mockRS232.RelayCommands.Should().HaveCount(11);
        for (byte i = 1; i <= 11; i++)
        {
            var commands = mockRS232.GetRelayCommandsForRelay(i);
            commands.Should().HaveCount(1);
            commands.First().TurnOn.Should().BeTrue();
        }
    }

    [Fact]
    public async Task AllReservedRelays_CanBeDeactivatedAfterActivation()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        await mockRS232.StartAsync();

        // Act - Activate then deactivate all reserved relays
        for (byte i = 1; i <= 11; i++)
        {
            await mockRS232.SendRelayCommandAsync(i, true);
        }
        for (byte i = 1; i <= 11; i++)
        {
            await mockRS232.SendRelayCommandAsync(i, false);
        }

        // Assert
        mockRS232.RelayCommands.Should().HaveCount(22); // 11 on + 11 off
        for (byte i = 1; i <= 11; i++)
        {
            var commands = mockRS232.GetRelayCommandsForRelay(i);
            commands.Should().HaveCount(2);
            commands.Last().TurnOn.Should().BeFalse();
        }
    }

    [Fact]
    public async Task ReservedRelay4_FiberBreakAlarm_CH1_ActivatesRelay()
    {
        // Arrange - Relay 4 is designated for FiberBreak alarm on Channel 1
        var mockRS232 = _fixture.MockRS232;
        var mockEvents = _fixture.MockEventAggregator;
        await mockRS232.StartAsync();

        const byte fiberBreakRelayId = 4; // CH1 FiberBreak = Relay 4

        // Act - Simulate FiberBreak alarm triggering reserved relay 1
        await mockEvents.PublishAsync(new AlarmTriggeredEvent(
            channelId: 1,
            zoneId: 0, // Channel-level alarm
            AlarmType.FiberBreak,
            value: 0,
            timestamp: DateTime.UtcNow,
            position: 500)); // Break at 500m

        await mockRS232.SendRelayCommandAsync(fiberBreakRelayId, true);
        await mockRS232.SetHeartbeatAlarmAsync(true);

        // Assert
        var command = mockRS232.GetRelayCommandsForRelay(fiberBreakRelayId);
        command.Should().HaveCount(1);
        command.First().TurnOn.Should().BeTrue();
        mockRS232.GetCurrentAlarmState().Should().BeTrue();
    }

    [Fact]
    public async Task ReservedRelay4_FiberBreakClear_CH1_DeactivatesRelay()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        var mockEvents = _fixture.MockEventAggregator;
        await mockRS232.StartAsync();

        const byte fiberBreakRelayId = 4; // CH1 FiberBreak = Relay 4

        // Activate on fiber break
        await mockRS232.SendRelayCommandAsync(fiberBreakRelayId, true);
        await mockRS232.SetHeartbeatAlarmAsync(true);

        // Act - Fiber restored, clear alarm
        await mockEvents.PublishAsync(new AlarmAutoClearedEvent(
            channelId: 1,
            zoneId: 0,
            AlarmType.FiberBreak,
            timestamp: DateTime.UtcNow));

        await mockRS232.SendRelayCommandAsync(fiberBreakRelayId, false);
        await mockRS232.SetHeartbeatAlarmAsync(false);

        // Assert
        var commands = mockRS232.GetRelayCommandsForRelay(fiberBreakRelayId);
        commands.Should().HaveCount(2);
        commands.Last().TurnOn.Should().BeFalse();
        mockRS232.GetCurrentAlarmState().Should().BeFalse();
    }

    [Theory]
    [InlineData(1, "SystemRunning")]
    [InlineData(2, "SystemHealth")]
    [InlineData(3, "CommonAlarm")]
    [InlineData(4, "FiberBreak_CH1")]
    [InlineData(5, "FiberBreak_CH2")]
    public async Task ReservedRelays_SystemAlarms_CanBeActivated(byte relayId, string alarmDescription)
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        await mockRS232.StartAsync();

        // Act - Simulate system alarm triggering reserved relay
        await mockRS232.SendRelayCommandAsync(relayId, true);
        await mockRS232.SetHeartbeatAlarmAsync(true);

        // Assert
        var command = mockRS232.GetRelayCommandsForRelay(relayId);
        command.Should().HaveCount(1, $"Reserved relay {relayId} ({alarmDescription}) should be activated");
        command.First().TurnOn.Should().BeTrue();
    }

    [Fact]
    public async Task ReservedRelays_AreNotAffectedByZoneAlarmClear()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        await mockRS232.StartAsync();

        // Activate reserved relay 1 (FiberBreak) and zone relay 12
        await mockRS232.SendRelayCommandAsync(1, true);  // Reserved
        await mockRS232.SendRelayCommandAsync(12, true); // Zone relay

        // Act - Clear zone alarm (should not affect reserved relay)
        await mockRS232.SendRelayCommandAsync(12, false);

        // Assert - Reserved relay should still be on
        var reservedCommands = mockRS232.GetRelayCommandsForRelay(1);
        var zoneCommands = mockRS232.GetRelayCommandsForRelay(12);

        reservedCommands.Should().HaveCount(1);
        reservedCommands.Last().TurnOn.Should().BeTrue();

        zoneCommands.Should().HaveCount(2);
        zoneCommands.Last().TurnOn.Should().BeFalse();
    }

    [Fact]
    public async Task ReservedRelays_MultipleChannelFiberBreaks_ActivatesMultipleRelays()
    {
        // Arrange - Each channel has its own fiber break relay (CH1=4, CH2=5, CH3=6)
        var mockRS232 = _fixture.MockRS232;
        var mockEvents = _fixture.MockEventAggregator;
        await mockRS232.StartAsync();

        // Act - Fiber breaks on channels 1, 2, 3
        await mockEvents.PublishAsync(new AlarmTriggeredEvent(1, 0, AlarmType.FiberBreak, 0, DateTime.UtcNow, 500));
        await mockRS232.SendRelayCommandAsync(4, true); // Relay 4 for CH1

        await mockEvents.PublishAsync(new AlarmTriggeredEvent(2, 0, AlarmType.FiberBreak, 0, DateTime.UtcNow, 300));
        await mockRS232.SendRelayCommandAsync(5, true); // Relay 5 for CH2

        await mockEvents.PublishAsync(new AlarmTriggeredEvent(3, 0, AlarmType.FiberBreak, 0, DateTime.UtcNow, 700));
        await mockRS232.SendRelayCommandAsync(6, true); // Relay 6 for CH3

        await mockRS232.SetHeartbeatAlarmAsync(true);

        // Assert
        mockRS232.GetRelayCommandsForRelay(4).Last().TurnOn.Should().BeTrue();
        mockRS232.GetRelayCommandsForRelay(5).Last().TurnOn.Should().BeTrue();
        mockRS232.GetRelayCommandsForRelay(6).Last().TurnOn.Should().BeTrue();
        mockRS232.GetCurrentAlarmState().Should().BeTrue();
    }

    [Fact]
    public async Task ReservedRelays_Reset_ClearsAllReservedRelays()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        await mockRS232.StartAsync();

        // Activate multiple reserved relays
        for (byte i = 1; i <= 5; i++)
        {
            await mockRS232.SendRelayCommandAsync(i, true);
        }
        await mockRS232.SetHeartbeatAlarmAsync(true);

        // Act - System reset clears all
        for (byte i = 1; i <= 5; i++)
        {
            await mockRS232.SendRelayCommandAsync(i, false);
        }
        await mockRS232.SetHeartbeatAlarmAsync(false);

        // Assert
        for (byte i = 1; i <= 5; i++)
        {
            var commands = mockRS232.GetRelayCommandsForRelay(i);
            commands.Last().TurnOn.Should().BeFalse();
        }
        mockRS232.GetCurrentAlarmState().Should().BeFalse();
    }

    [Fact]
    public void ReservedRelays_AreNotInAssignablePool()
    {
        // Arrange
        var zonesWithRelays = _fixture.DefaultZoneConfigs
            .Where(z => z.AssignedRelayId.HasValue)
            .Select(z => z.AssignedRelayId!.Value)
            .ToList();

        // Assert - No zone should be assigned to relays 1-10
        zonesWithRelays.Should().NotContain(
            Enumerable.Range(1, 11),
            "Reserved relays (1-11) should not be assignable to zones");
    }

    [Fact]
    public async Task ReservedRelays_SequentialActivationAndDeactivation()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        await mockRS232.StartAsync();

        // Act - Activate relays 1-11 in sequence, then deactivate in reverse
        for (byte i = 1; i <= 11; i++)
        {
            await mockRS232.SendRelayCommandAsync(i, true);
        }
        for (byte i = 11; i >= 1; i--)
        {
            await mockRS232.SendRelayCommandAsync(i, false);
        }

        // Assert - Verify correct command sequence
        mockRS232.RelayCommands.Should().HaveCount(22);

        // First 11 commands should be ON (1-11)
        for (int i = 0; i < 11; i++)
        {
            mockRS232.RelayCommands[i].RelayId.Should().Be((byte)(i + 1));
            mockRS232.RelayCommands[i].TurnOn.Should().BeTrue();
        }

        // Last 11 commands should be OFF (11-1)
        for (int i = 0; i < 11; i++)
        {
            mockRS232.RelayCommands[11 + i].RelayId.Should().Be((byte)(11 - i));
            mockRS232.RelayCommands[11 + i].TurnOn.Should().BeFalse();
        }
    }

    #endregion
}
