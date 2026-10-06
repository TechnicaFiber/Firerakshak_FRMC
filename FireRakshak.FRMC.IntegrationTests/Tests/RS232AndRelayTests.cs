using FireRakshak.FRMC.IntegrationTests.Fixtures;
using FireRakshak.FRMC.IntegrationTests.Mocks;
using FluentAssertions;
using Xunit;

namespace FireRakshak.FRMC.IntegrationTests.Tests;

/// <summary>
/// Integration tests for RS232 serial communication and relay control.
/// Tests STM32 communication, relay commands, and heartbeat functionality.
/// </summary>
[Collection("FRMC Integration Tests")]
public class RS232AndRelayTests
{
    private readonly FrmcTestFixture _fixture;

    public RS232AndRelayTests(FrmcTestFixture fixture)
    {
        _fixture = fixture;
        _fixture.Reset();
    }

    [Fact]
    public async Task RS232Start_WhenCalled_StartsSuccessfully()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;

        // Act
        await mockRS232.StartAsync();

        // Assert
        mockRS232.IsRunning.Should().BeTrue();
        mockRS232.StartCallCount.Should().Be(1);
    }

    [Fact]
    public async Task RS232Stop_WhenRunning_StopsSuccessfully()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        await mockRS232.StartAsync();

        // Act
        await mockRS232.StopAsync();

        // Assert
        mockRS232.IsRunning.Should().BeFalse();
        mockRS232.StopCallCount.Should().Be(1);
    }

    [Fact]
    public async Task RelayCommand_WhenTurnOn_SendsCorrectCommand()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        await mockRS232.StartAsync();

        // Act
        await mockRS232.SendRelayCommandAsync(11, true);

        // Assert
        mockRS232.RelayCommands.Should().HaveCount(1);
        var cmd = mockRS232.GetLastRelayCommand();
        cmd.Should().NotBeNull();
        cmd!.RelayId.Should().Be(11);
        cmd.TurnOn.Should().BeTrue();
    }

    [Fact]
    public async Task RelayCommand_WhenTurnOff_SendsCorrectCommand()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        await mockRS232.StartAsync();

        // Act
        await mockRS232.SendRelayCommandAsync(15, false);

        // Assert
        var cmd = mockRS232.GetLastRelayCommand();
        cmd.Should().NotBeNull();
        cmd!.RelayId.Should().Be(15);
        cmd.TurnOn.Should().BeFalse();
    }

    [Fact]
    public async Task RelayCommand_WhenNotRunning_ThrowsException()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        // Don't start

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => mockRS232.SendRelayCommandAsync(11, true));
    }

    [Fact]
    public async Task HeartbeatAlarm_WhenSet_UpdatesState()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        await mockRS232.StartAsync();

        // Act
        await mockRS232.SetHeartbeatAlarmAsync(true);

        // Assert
        mockRS232.GetCurrentAlarmState().Should().BeTrue();
        mockRS232.HeartbeatAlarmStates.Should().Contain(true);
    }

    [Fact]
    public async Task HeartbeatAlarm_WhenCleared_UpdatesState()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        await mockRS232.StartAsync();
        await mockRS232.SetHeartbeatAlarmAsync(true);

        // Act
        await mockRS232.SetHeartbeatAlarmAsync(false);

        // Assert
        mockRS232.GetCurrentAlarmState().Should().BeFalse();
        mockRS232.HeartbeatAlarmStates.Should().HaveCount(2);
        mockRS232.HeartbeatAlarmStates.Last().Should().BeFalse();
    }

    [Fact]
    public async Task MultipleRelayCommands_WhenSent_TracksAll()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        await mockRS232.StartAsync();

        // Act
        await mockRS232.SendRelayCommandAsync(11, true);
        await mockRS232.SendRelayCommandAsync(12, true);
        await mockRS232.SendRelayCommandAsync(13, true);
        await mockRS232.SendRelayCommandAsync(11, false);

        // Assert
        mockRS232.RelayCommands.Should().HaveCount(4);

        var relay11Commands = mockRS232.GetRelayCommandsForRelay(11);
        relay11Commands.Should().HaveCount(2);
        relay11Commands.First().TurnOn.Should().BeTrue();
        relay11Commands.Last().TurnOn.Should().BeFalse();
    }

    [Fact]
    public async Task RelayCommandSequence_WhenAlarmTriggered_ActivatesCorrectRelay()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        await mockRS232.StartAsync();

        // Simulate alarm trigger sequence
        // 1. Set heartbeat alarm
        await mockRS232.SetHeartbeatAlarmAsync(true);

        // 2. Activate zone relay (e.g., zone 1 has relay 11)
        await mockRS232.SendRelayCommandAsync(11, true);

        // Assert
        mockRS232.GetCurrentAlarmState().Should().BeTrue();
        mockRS232.RelayCommands.Should().HaveCount(1);
        mockRS232.GetLastRelayCommand()!.RelayId.Should().Be(11);
        mockRS232.GetLastRelayCommand()!.TurnOn.Should().BeTrue();
    }

    [Fact]
    public async Task RelayCommandSequence_WhenAlarmCleared_DeactivatesRelay()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        await mockRS232.StartAsync();

        // Setup: alarm active
        await mockRS232.SetHeartbeatAlarmAsync(true);
        await mockRS232.SendRelayCommandAsync(11, true);

        // Act: clear alarm
        await mockRS232.SendRelayCommandAsync(11, false);
        await mockRS232.SetHeartbeatAlarmAsync(false);

        // Assert
        mockRS232.GetCurrentAlarmState().Should().BeFalse();
        var relay11Commands = mockRS232.GetRelayCommandsForRelay(11);
        relay11Commands.Last().TurnOn.Should().BeFalse();
    }

    [Fact]
    public async Task RelayIdRange_WhenValid1Based_AcceptsCommand()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        await mockRS232.StartAsync();

        // Act - Test 1-based relay IDs (valid range 1-255)
        await mockRS232.SendRelayCommandAsync(1, true);   // Min valid
        await mockRS232.SendRelayCommandAsync(48, true);  // Typical max
        await mockRS232.SendRelayCommandAsync(255, true); // Max valid

        // Assert
        mockRS232.RelayCommands.Should().HaveCount(3);
        mockRS232.RelayCommands.Select(c => c.RelayId)
            .Should().BeEquivalentTo(new byte[] { 1, 48, 255 });
    }

    [Fact]
    public async Task FixedRelays_Range1To10_AreReserved()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        await mockRS232.StartAsync();

        // Act - Fixed relays 1-10 should still accept commands
        // (they're reserved but not disabled)
        for (byte i = 1; i <= 10; i++)
        {
            await mockRS232.SendRelayCommandAsync(i, true);
        }

        // Assert
        mockRS232.RelayCommands.Should().HaveCount(10);
    }

    [Fact]
    public async Task ZoneAssignableRelays_Range11To48_AreAvailable()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        await mockRS232.StartAsync();

        // Act - Relays 11-48 are available for zone assignment
        await mockRS232.SendRelayCommandAsync(11, true);
        await mockRS232.SendRelayCommandAsync(25, true);
        await mockRS232.SendRelayCommandAsync(48, true);

        // Assert
        mockRS232.RelayCommands.Should().HaveCount(3);
    }

    [Fact]
    public void Reset_ClearsAllState()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;

        // Act
        mockRS232.Reset();

        // Assert
        mockRS232.RelayCommands.Should().BeEmpty();
        mockRS232.HeartbeatAlarmStates.Should().BeEmpty();
        mockRS232.StartCallCount.Should().Be(0);
        mockRS232.StopCallCount.Should().Be(0);
    }

    [Fact]
    public async Task ConcurrentRelayCommands_WhenSentRapidly_AllProcessed()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        await mockRS232.StartAsync();

        // Act - Send multiple commands concurrently
        var tasks = new List<Task>();
        for (byte i = 11; i <= 20; i++)
        {
            var relayId = i;
            tasks.Add(mockRS232.SendRelayCommandAsync(relayId, true));
        }
        await Task.WhenAll(tasks);

        // Assert
        mockRS232.RelayCommands.Should().HaveCount(10);
    }

    [Fact]
    public async Task RelayCommandTimestamp_WhenSent_RecordsTime()
    {
        // Arrange
        var mockRS232 = _fixture.MockRS232;
        await mockRS232.StartAsync();
        var beforeTime = DateTime.UtcNow;

        // Act
        await mockRS232.SendRelayCommandAsync(11, true);
        var afterTime = DateTime.UtcNow;

        // Assert
        var cmd = mockRS232.GetLastRelayCommand();
        cmd!.Timestamp.Should().BeOnOrAfter(beforeTime);
        cmd.Timestamp.Should().BeOnOrBefore(afterTime);
    }
}
