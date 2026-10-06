using System.Threading.Tasks;

namespace FireRakshak.FRMC.Managers.ExternalCommunicationManager;

/// <summary>
/// Defines the contract for managing external communication servers like Modbus TCP and public WebSockets.
/// </summary>
public interface IExternalCommunicationManager
{
    /// <summary>
    /// Starts the Modbus TCP server.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task StartModbusServerAsync();

    /// <summary>
    /// Starts the public WebSocket server.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task StartWebSocketServerAsync();

    /// <summary>
    /// Stops all managed external communication servers.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</n    Task StopAllServersAsync();
}
