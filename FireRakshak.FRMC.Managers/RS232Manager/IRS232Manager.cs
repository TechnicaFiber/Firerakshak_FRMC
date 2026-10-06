using System.Threading.Tasks;

namespace FireRakshak.FRMC.Managers.RS232Manager
{
    /// <summary>
    /// Interface for RS232 serial communication manager.
    /// </summary>
    public interface IRS232Manager
    {
        /// <summary>
        /// Opens the serial port and starts listening for commands.
        /// </summary>
        Task StartAsync();

        /// <summary>
        /// Stops listening and closes the serial port.
        /// </summary>
        Task StopAsync();

        /// <summary>
        /// Indicates whether the serial port is open and listening.
        /// </summary>
        
        bool IsRunning { get; }
        Task SendRelayCommandAsync(byte relayId, bool turnOn);
        Task SetHeartbeatAlarmAsync(bool alarmOn);
       

    }
}
