using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace FireRakshak.FRMC.Managers.ExternalCommunicationManager;

/// <summary>
/// Implements a non-authenticated WebSocket server for third-party UI or public data access.
/// </summary>
public class WebSocketPublicServer
{
    private readonly ILogger<WebSocketPublicServer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebSocketPublicServer"/> class.
    /// </summary>
    /// <param name="logger">The logger for recording information.</param>
    public WebSocketPublicServer(ILogger<WebSocketPublicServer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Starts the public WebSocket server.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task StartAsync()
    {
        _logger.LogInformation("Public WebSocket Server starting.");
        // TODO: Implement actual WebSocket server startup logic (non-authenticated)
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the public WebSocket server.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task StopAsync()
    {
        _logger.LogInformation("Public WebSocket Server stopping.");
        // TODO: Implement actual WebSocket server shutdown logic
        return Task.CompletedTask;
    }
}
