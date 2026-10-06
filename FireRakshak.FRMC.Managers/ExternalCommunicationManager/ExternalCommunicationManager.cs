using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace FireRakshak.FRMC.Managers.ExternalCommunicationManager;

/// <summary>
/// Manages external communication servers, including Modbus TCP and public WebSockets.
/// </summary>
public class ExternalCommunicationManager : IExternalCommunicationManager
{
    private readonly ModbusTcpServer _modbusTcpServer;
    private readonly WebSocketPublicServer _webSocketPublicServer;
    private readonly ILogger<ExternalCommunicationManager> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExternalCommunicationManager"/> class.
    /// </summary>
    /// <param name="modbusTcpServer">The Modbus TCP server instance.</param>
    /// <param name="webSocketPublicServer">The public WebSocket server instance.</param>
    /// <param name="logger">The logger for recording information.</param>
    public ExternalCommunicationManager(ModbusTcpServer modbusTcpServer, WebSocketPublicServer webSocketPublicServer, ILogger<ExternalCommunicationManager> logger)
    {
        _modbusTcpServer = modbusTcpServer;
        _webSocketPublicServer = webSocketPublicServer;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task StartModbusServerAsync()
    {
        _logger.LogInformation("Starting Modbus TCP Server.");
        await _modbusTcpServer.StartAsync();
    }

    /// <inheritdoc/>
    public async Task StartWebSocketServerAsync()
    {
        _logger.LogInformation("Starting Public WebSocket Server.");
        await _webSocketPublicServer.StartAsync();
    }

    /// <inheritdoc/>
    public async Task StopAllServersAsync()
    {
        _logger.LogInformation("Stopping all external communication servers.");
        await _modbusTcpServer.StopAsync();
        await _webSocketPublicServer.StopAsync();
    }
}
