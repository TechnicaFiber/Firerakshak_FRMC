using FireRakshak.FRMC.Managers.RS232Manager;

namespace FireRakshak.FRMC.IntegrationTests.Mocks;

/// <summary>
/// Mock RS232 Manager for integration testing.
/// Simulates STM32 serial communication for relay control and heartbeat.
/// Tracks all commands for test assertions.
/// </summary>
public class MockRS232Manager : IRS232Manager
{
    private bool _isRunning;
    private bool _alarmState;

    // Command tracking for test assertions
    public List<RelayCommand> RelayCommands { get; } = new();
    public List<bool> HeartbeatAlarmStates { get; } = new();
    public int StartCallCount { get; private set; }
    public int StopCallCount { get; private set; }

    // Simulation events for external triggers
    public event EventHandler? LaserButtonPressed;
    public event EventHandler? ResetButtonPressed;
    public event EventHandler? ShutdownRequested;

    public bool IsRunning => _isRunning;

    public Task StartAsync()
    {
        StartCallCount++;
        _isRunning = true;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        StopCallCount++;
        _isRunning = false;
        return Task.CompletedTask;
    }

    public Task SendRelayCommandAsync(byte relayId, bool turnOn)
    {
        if (!_isRunning)
            throw new InvalidOperationException("RS232 not running");

        var command = new RelayCommand
        {
            RelayId = relayId,
            TurnOn = turnOn,
            Timestamp = DateTime.UtcNow
        };

        RelayCommands.Add(command);
        return Task.CompletedTask;
    }

    public Task SetHeartbeatAlarmAsync(bool alarmOn)
    {
        _alarmState = alarmOn;
        HeartbeatAlarmStates.Add(alarmOn);
        return Task.CompletedTask;
    }

    // Test helper methods to simulate STM32 button presses
    public void SimulateLaserButtonPress()
    {
        LaserButtonPressed?.Invoke(this, EventArgs.Empty);
    }

    public void SimulateResetButtonPress()
    {
        ResetButtonPressed?.Invoke(this, EventArgs.Empty);
    }

    public void SimulateShutdownRequest()
    {
        ShutdownRequested?.Invoke(this, EventArgs.Empty);
    }

    public bool GetCurrentAlarmState() => _alarmState;

    public RelayCommand? GetLastRelayCommand()
    {
        return RelayCommands.LastOrDefault();
    }

    public List<RelayCommand> GetRelayCommandsForRelay(byte relayId)
    {
        return RelayCommands.Where(c => c.RelayId == relayId).ToList();
    }

    public void Reset()
    {
        _isRunning = false; // Reset running state
        RelayCommands.Clear();
        HeartbeatAlarmStates.Clear();
        StartCallCount = 0;
        StopCallCount = 0;
        _alarmState = false;
    }
}

public class RelayCommand
{
    public byte RelayId { get; set; }
    public bool TurnOn { get; set; }
    public DateTime Timestamp { get; set; }
}
