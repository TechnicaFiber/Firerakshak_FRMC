using FireRakshak.FRMC.Core.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using System.Net.Sockets;

namespace FireRakshak.FRMC.Managers.ServiceManager;

/// <summary>
/// Dedicated WebSocket server for live temperature data streaming.
/// Separated from the control WebSocket to prevent data streaming from blocking
/// command/request processing and health status broadcasts.
///
/// Architecture:
/// - Port 6164: Control WebSocket (commands, config, health, alarms)
/// - Port 6165: Data WebSocket (temperature streaming only)
/// </summary>
public class DataStreamServer : IDataStreamServer
{
    private readonly ILogger<DataStreamServer> _logger;
    private readonly int _port;

    private FrmcConfiguration? _frmcConfig;
    private TcpListener? _tcpListener;
    private X509Certificate2? _serverCertificate;

    private HttpListener? _httpListener;
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;

    // Connected clients
    private readonly ConcurrentDictionary<WebSocket, DataStreamClient> _clients = new();

    // Channel subscriptions - maps WebSocket to subscribed channel IDs
    private readonly ConcurrentDictionary<WebSocket, ConcurrentBag<int>> _channelSubscriptions = new();

    // Send locks to prevent concurrent writes on same WebSocket
    private readonly ConcurrentDictionary<WebSocket, SemaphoreSlim> _sendLocks = new();

    // Flag to control whether streaming is active
    private volatile bool _isStreamingActive = false;
    public bool IsStreamingActive => _isStreamingActive;

    public DataStreamServer(ILogger<DataStreamServer> logger, int port = 6165)
    {
        _logger = logger;
        _port = port;
    }

    /// <summary>
    /// Start the data streaming server.
    /// Listens on all interfaces to accept connections from remote DTSCM clients.
    /// Note: May require URL ACL registration on Windows for non-localhost prefixes:
    ///   netsh http add urlacl url=http://+:6165/data_stream/ user=Everyone
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("DataStreamServer starting on port {Port}...", _port);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken linkedToken = _cts.Token;

        // Load configuration
        _frmcConfig = FrmcConfiguration.LoadFromFile("config.json") ?? new FrmcConfiguration();

        // Default WSS configuration if not present in config.json
        if (string.IsNullOrWhiteSpace(_frmcConfig.CertificatePath))
        {
            _frmcConfig.CertificatePath = "localhost.pfx";
        }

        if (string.IsNullOrWhiteSpace(_frmcConfig.CertificatePassword))
        {
            _frmcConfig.CertificatePassword = "FRMC@123";
        }

#if USE_SECURE
        _frmcConfig.UseSecure = true;
#else
        _frmcConfig.UseSecure = false;
#endif

        if (_frmcConfig.UseSecure)
        {
            _logger.LogInformation("DataStreamServer starting in WSS (Secure) mode.");

            // Load certificate
            try
            {
                if (string.IsNullOrEmpty(_frmcConfig.CertificatePath))
                {
                    throw new ArgumentException("CertificatePath is empty or null in FrmcConfiguration when UseSecure is true.");
                }

                if (string.IsNullOrEmpty(_frmcConfig.CertificatePassword))
                {
                    _serverCertificate = new X509Certificate2(_frmcConfig.CertificatePath);
                }
                else
                {
                    _serverCertificate = new X509Certificate2(_frmcConfig.CertificatePath, _frmcConfig.CertificatePassword);
                }

                _logger.LogInformation("Successfully loaded SSL/TLS certificate from {Path}", _frmcConfig.CertificatePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load SSL/TLS certificate from {Path}", _frmcConfig.CertificatePath ?? "null");
                throw;
            }

            // Start WSS/TCP Listener
            try
            {
                _tcpListener = new TcpListener(IPAddress.IPv6Any, _port);
                _tcpListener.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
                    _tcpListener.Start();
                    _logger.LogInformation("WSS TCP Listener started on port {Port}", _port);
                    _listenerTask = Task.Run(() => StartListeningTcpAsync(_tcpListener, linkedToken), linkedToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start WSS TCP Listener");
                throw;
            }
        }
        else
        {
            _logger.LogInformation("DataStreamServer starting in WS (Unsecure) mode.");
            try
            {
                _httpListener = new HttpListener();

                // Try to listen on all interfaces first (for remote DTSCM connections)
                // Falls back to localhost if URL ACL is not registered
                string prefix = $"http://+:{_port}/data_stream/";
                try
                {
                    _httpListener.Prefixes.Add(prefix);
                    _httpListener.Start();
                    _logger.LogInformation("DataStreamServer started on {Prefix} (accepting remote connections)", prefix);
                }
                catch (HttpListenerException)
                {
                    // Fall back to localhost (no admin/URL ACL required)
                    _httpListener = new HttpListener();
                    prefix = $"http://localhost:{_port}/data_stream/";
                    _httpListener.Prefixes.Add(prefix);
                    _httpListener.Start();
                    _logger.LogWarning("DataStreamServer started on {Prefix} (localhost only - register URL ACL for remote access)", prefix);
                }

                _listenerTask = Task.Run(() => ListenForConnectionsAsync(_cts.Token), _cts.Token);
            }
            catch (HttpListenerException ex)
            {
                _logger.LogError(ex, "Failed to start DataStreamServer on port {Port}", _port);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stop the data streaming server.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("DataStreamServer stopping...");
        _cts?.Cancel();

        // Close all client connections
        foreach (var kvp in _clients.ToArray())
        {
            try
            {
                if (kvp.Key.State == WebSocketState.Open)
                {
                    await kvp.Key.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server shutting down", CancellationToken.None);
                }
            }
            catch { /* ignore */ }
        }
        _clients.Clear();
        _channelSubscriptions.Clear();

        // Dispose send locks
        foreach (var kvp in _sendLocks)
        {
            kvp.Value.Dispose();
        }
        _sendLocks.Clear();

        try
        {
            if (_listenerTask != null) await _listenerTask.ConfigureAwait(false);
        }
        catch { /* ignore */ }

        if (_tcpListener != null)
        {
            try
            {
                _tcpListener.Stop();
                _logger.LogInformation("WSS TCP Listener stopped.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping WSS TCP Listener");
            }
        }

        if (_httpListener?.IsListening == true)
        {
            _httpListener.Stop();
            _httpListener.Close();
        }

        _cts?.Dispose();
        _logger.LogInformation("DataStreamServer stopped.");
    }

    private async Task ListenForConnectionsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _httpListener?.IsListening == true)
        {
            try
            {
                var context = await _httpListener.GetContextAsync().ConfigureAwait(false);

                if (context.Request.IsWebSocketRequest)
                {
                    _ = HandleConnectionAsync(context, cancellationToken);
                }
                else
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                }
            }
            catch (HttpListenerException)
            {
                break; // Listener stopped
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accepting DataStream connection");
            }
        }
    }

    private async Task StartListeningTcpAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var tcpClient = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = HandleTcpClientAsync(tcpClient, cancellationToken);
            }
            catch (ObjectDisposedException)
            {
                break; // Listener stopped
            }
            catch (Exception ex)
            {
                if (cancellationToken.IsCancellationRequested) break;
                _logger.LogError(ex, "Error accepting TCP client connection");
            }
        }
    }

    private async Task HandleTcpClientAsync(TcpClient tcpClient, CancellationToken cancellationToken)
    {
        string remoteEndpoint = "unknown";
        try
        {
            remoteEndpoint = tcpClient.Client.RemoteEndPoint?.ToString() ?? "unknown";
        }
        catch { }

        _logger.LogInformation("Accepted TCP client connection from {Endpoint}", remoteEndpoint);

        try
        {
            Stream stream = tcpClient.GetStream();

            // Peek at the first byte to detect if this is a TLS/SSL handshake
            byte[] peekBuffer = new byte[1];
            int peeked = tcpClient.Client.Receive(peekBuffer, 0, 1, SocketFlags.Peek);
            bool isTls = peeked > 0 && peekBuffer[0] == 0x16;

            if (isTls)
            {
                if (_serverCertificate == null)
                {
                    _logger.LogWarning("Incoming secure connection detected but no SSL certificate is loaded. Closing connection.");
                    tcpClient.Close();
                    return;
                }

                var sslStream = new SslStream(stream, false);
                try
                {
                    await sslStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _serverCertificate,
                        ClientCertificateRequired = false,
                        EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                    }, cancellationToken).ConfigureAwait(false);
                    stream = sslStream;
                    _logger.LogInformation("TLS handshake completed successfully for client {Endpoint}", remoteEndpoint);
                }
                catch (Exception tlsEx)
                {
                    _logger.LogError(tlsEx, "TLS handshake failed for client {Endpoint}", remoteEndpoint);
                    tcpClient.Close();
                    return;
                }
            }
            else
            {
                _logger.LogInformation("Plain HTTP/WebSocket connection detected for client {Endpoint}", remoteEndpoint);
            }

            // Parse HTTP Upgrade request
            var (path, secWebSocketKey) = await ParseHttpHandshakeAsync(stream, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(secWebSocketKey) || !path.StartsWith("/data_stream/"))
            {
                _logger.LogWarning("Invalid HTTP Upgrade handshake request from client {Endpoint}. Path: {Path}", remoteEndpoint, path ?? "null");
                byte[] response = Encoding.UTF8.GetBytes("HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response, 0, response.Length, cancellationToken).ConfigureAwait(false);
                tcpClient.Close();
                return;
            }

            // Compute Accept Key
            string key = secWebSocketKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
            byte[] hash = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(key));
            string acceptKey = Convert.ToBase64String(hash);

            string responseString = "HTTP/1.1 101 Switching Protocols\r\n" +
                                   "Upgrade: websocket\r\n" +
                                   "Connection: Upgrade\r\n" +
                                   $"Sec-WebSocket-Accept: {acceptKey}\r\n\r\n";
            byte[] responseBytes = Encoding.UTF8.GetBytes(responseString);
            await stream.WriteAsync(responseBytes, 0, responseBytes.Length, cancellationToken).ConfigureAwait(false);

            // Upgrade Stream to standard WebSocket
            using var webSocket = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));

            _logger.LogInformation("WebSocket upgraded successfully for client {Endpoint}", remoteEndpoint);

            await ProcessConnectionAsync(webSocket, remoteEndpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Client connection handling failed for client {Endpoint}", remoteEndpoint);
            tcpClient.Close();
        }
    }

    private async Task<(string? Path, string? SecWebSocketKey)> ParseHttpHandshakeAsync(Stream stream, CancellationToken cancellationToken)
    {
        var headerBytes = new List<byte>();
        byte[] tempBuffer = new byte[1];
        while (!cancellationToken.IsCancellationRequested)
        {
            int read = await stream.ReadAsync(tempBuffer, 0, 1, cancellationToken).ConfigureAwait(false);
            if (read <= 0) break;
            headerBytes.Add(tempBuffer[0]);
            if (headerBytes.Count >= 4 &&
                headerBytes[^1] == 10 && headerBytes[^2] == 13 &&
                headerBytes[^3] == 10 && headerBytes[^4] == 13)
            {
                break;
            }
            if (headerBytes.Count > 8192)
            {
                throw new InvalidOperationException("HTTP headers too large");
            }
        }

        string headerText = Encoding.UTF8.GetString(headerBytes.ToArray());
        var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
        if (lines.Length == 0) return (null, null);

        var requestParts = lines[0].Split(' ');
        if (requestParts.Length < 2 || requestParts[0] != "GET")
        {
            return (null, null);
        }
        string path = requestParts[1];

        string? secWebSocketKey = null;
        bool hasUpgrade = false;
        bool hasConnection = false;

        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            int colonIndex = line.IndexOf(':');
            if (colonIndex <= 0) continue;

            string headerName = line.Substring(0, colonIndex).Trim().ToLower();
            string headerValue = line.Substring(colonIndex + 1).Trim();

            if (headerName == "upgrade" && headerValue.Equals("websocket", StringComparison.OrdinalIgnoreCase))
            {
                hasUpgrade = true;
            }
            else if (headerName == "connection" && headerValue.Contains("Upgrade", StringComparison.OrdinalIgnoreCase))
            {
                hasConnection = true;
            }
            else if (headerName == "sec-websocket-key")
            {
                secWebSocketKey = headerValue;
            }
        }

        if (hasUpgrade && hasConnection && !string.IsNullOrEmpty(secWebSocketKey))
        {
            return (path, secWebSocketKey);
        }

        return (null, null);
    }

    private async Task HandleConnectionAsync(HttpListenerContext httpContext, CancellationToken cancellationToken)
    {
        WebSocket? webSocket = null;
        try
        {
            var wsContext = await httpContext.AcceptWebSocketAsync(null);
            webSocket = wsContext.WebSocket;
            var remoteEndpoint = httpContext.Request.RemoteEndPoint?.ToString() ?? "unknown";
            await ProcessConnectionAsync(webSocket, remoteEndpoint, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling DataStream connection");
            try
            {
                httpContext.Response.StatusCode = 500;
                httpContext.Response.Close();
            }
            catch { }
        }
    }

    private async Task ProcessConnectionAsync(WebSocket webSocket, string remoteEndpoint, CancellationToken cancellationToken)
    {
        try
        {
            var clientInfo = new DataStreamClient
            {
                ConnectedAt = DateTime.UtcNow,
                RemoteEndpoint = remoteEndpoint
            };

            _clients.TryAdd(webSocket, clientInfo);
            _channelSubscriptions.TryAdd(webSocket, new ConcurrentBag<int>());
            _sendLocks.TryAdd(webSocket, new SemaphoreSlim(1, 1));

            _logger.LogInformation("DataStream client connected from {Endpoint}. Total clients: {Count}",
                remoteEndpoint, _clients.Count);

            // Send welcome message
            await SendMessageAsync(webSocket, new WebSocketMessage("data_stream_connected",
                JsonSerializer.SerializeToElement(new { message = "Connected to FRMC Data Stream", timestamp = DateTime.UtcNow })));

            // Handle incoming messages (subscriptions)
            await ReceiveMessagesAsync(webSocket, cancellationToken);
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning("DataStream WebSocket error for {Endpoint}: {Message}", remoteEndpoint, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling DataStream connection for {Endpoint}", remoteEndpoint);
        }
        finally
        {
            await RemoveClientAsync(webSocket);
        }
    }

    private async Task ReceiveMessagesAsync(WebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];

        while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    await HandleClientMessageAsync(webSocket, message);
                }
            }
            catch (WebSocketException)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task HandleClientMessageAsync(WebSocket webSocket, string message)
    {
        try
        {
            var json = JsonDocument.Parse(message);
            var root = json.RootElement;

            if (!root.TryGetProperty("type", out var typeElement))
            {
                _logger.LogWarning("DataStream message missing 'type' field");
                return;
            }

            var messageType = typeElement.GetString();

            switch (messageType)
            {
                case "subscribe_channel":
                    await HandleSubscribeChannel(webSocket, root);
                    break;

                case "unsubscribe_channel":
                    await HandleUnsubscribeChannel(webSocket, root);
                    break;

                case "start_streaming":
                    _isStreamingActive = true;
                    _logger.LogInformation("DataStream: Streaming ENABLED by client request");
                    await SendMessageAsync(webSocket, new WebSocketMessage("streaming_started",
                        JsonSerializer.SerializeToElement(new { success = true, timestamp = DateTime.UtcNow })));
                    break;

                case "stop_streaming":
                    _isStreamingActive = false;
                    _logger.LogInformation("DataStream: Streaming DISABLED by client request");
                    await SendMessageAsync(webSocket, new WebSocketMessage("streaming_stopped",
                        JsonSerializer.SerializeToElement(new { success = true, timestamp = DateTime.UtcNow })));
                    break;

                case "ping":
                    await SendMessageAsync(webSocket, new WebSocketMessage("pong",
                        JsonSerializer.SerializeToElement(new { timestamp = DateTime.UtcNow })));
                    break;

                default:
                    _logger.LogDebug("DataStream: Unknown message type '{Type}'", messageType);
                    break;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("DataStream: Invalid JSON message: {Error}", ex.Message);
        }
    }

    private Task HandleSubscribeChannel(WebSocket webSocket, JsonElement root)
    {
        if (root.TryGetProperty("channelId", out var channelIdElement))
        {
            var channelId = channelIdElement.GetInt32();

            if (_channelSubscriptions.TryGetValue(webSocket, out var channels))
            {
                if (!channels.Contains(channelId))
                {
                    channels.Add(channelId);
                    _logger.LogInformation("DataStream: Client subscribed to channel {ChannelId}", channelId);
                }
            }
        }
        return Task.CompletedTask;
    }

    private Task HandleUnsubscribeChannel(WebSocket webSocket, JsonElement root)
    {
        if (root.TryGetProperty("channelId", out var channelIdElement))
        {
            var channelId = channelIdElement.GetInt32();

            if (_channelSubscriptions.TryGetValue(webSocket, out var oldBag))
            {
                // Create new bag without the channel
                var newBag = new ConcurrentBag<int>(oldBag.Where(id => id != channelId));
                _channelSubscriptions.TryUpdate(webSocket, newBag, oldBag);
                _logger.LogInformation("DataStream: Client unsubscribed from channel {ChannelId}", channelId);
            }
        }
        return Task.CompletedTask;
    }

    private async Task RemoveClientAsync(WebSocket webSocket)
    {
        _clients.TryRemove(webSocket, out _);
        _channelSubscriptions.TryRemove(webSocket, out _);

        if (_sendLocks.TryRemove(webSocket, out var sendLock))
        {
            sendLock.Dispose();
        }

        try
        {
            if (webSocket.State == WebSocketState.Open)
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnected", CancellationToken.None);
            }
            webSocket.Dispose();
        }
        catch { /* ignore */ }

        _logger.LogInformation("DataStream client disconnected. Remaining: {Count}", _clients.Count);

        // Disable streaming if no clients
        if (_clients.IsEmpty && _isStreamingActive)
        {
            _isStreamingActive = false;
            _logger.LogInformation("DataStream: No clients connected - streaming DISABLED");
        }
    }

    /// <summary>
    /// Enable streaming (called by control WebSocket when measurement starts).
    /// </summary>
    public void EnableStreaming()
    {
        _isStreamingActive = true;
        _logger.LogInformation("DataStream: Streaming ENABLED");
    }

    /// <summary>
    /// Disable streaming (called by control WebSocket when measurement stops).
    /// </summary>
    public void DisableStreaming()
    {
        _isStreamingActive = false;
        _logger.LogInformation("DataStream: Streaming DISABLED");
    }

    /// <summary>
    /// Broadcast temperature data to all clients subscribed to the specified channel.
    /// </summary>
    public async Task BroadcastToChannelSubscribersAsync(int channelId, WebSocketMessage message)
    {
        if (!_isStreamingActive)
        {
            _logger.LogDebug("DataStream: Streaming not active, skipping broadcast for channel {ChannelId}", channelId);
            return;
        }

        // Get subscribed clients
        List<WebSocket> subscribedClients;
        try
        {
            var snapshot = _channelSubscriptions.ToArray();
            subscribedClients = snapshot
                .Where(kvp => kvp.Value.Contains(channelId))
                .Select(kvp => kvp.Key)
                .Where(ws => ws.State == WebSocketState.Open)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DataStream: Error getting subscribed clients for channel {ChannelId}", channelId);
            return;
        }

        if (subscribedClients.Count == 0)
        {
            _logger.LogDebug("DataStream: No clients subscribed to channel {ChannelId}", channelId);
            return;
        }

        _logger.LogDebug("DataStream: Broadcasting to {Count} clients for channel {ChannelId}",
            subscribedClients.Count, channelId);

        // Broadcast to all subscribed clients
        var sendTasks = subscribedClients.Select(ws => SendMessageAsync(ws, message));
        await Task.WhenAll(sendTasks);
    }

    /// <summary>
    /// Broadcast pre-serialized binary data to all clients subscribed to the specified channel.
    /// Used for temperature data (binary protocol) — avoids JSON serialization entirely.
    /// </summary>
    public async Task BroadcastBinaryToChannelSubscribersAsync(int channelId, byte[] data)
    {
        if (!_isStreamingActive)
            return;

        List<WebSocket> subscribedClients;
        try
        {
            var snapshot = _channelSubscriptions.ToArray();
            subscribedClients = snapshot
                .Where(kvp => kvp.Value.Contains(channelId))
                .Select(kvp => kvp.Key)
                .Where(ws => ws.State == WebSocketState.Open)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DataStream: Error getting subscribed clients for binary broadcast ch {ChannelId}", channelId);
            return;
        }

        if (subscribedClients.Count == 0)
            return;

        var segment = new ArraySegment<byte>(data);
        var sendTasks = subscribedClients.Select(ws => SendBinaryAsync(ws, segment));
        await Task.WhenAll(sendTasks);
    }

    /// <summary>
    /// Send binary data to a specific WebSocket client.
    /// </summary>
    private async Task SendBinaryAsync(WebSocket webSocket, ArraySegment<byte> data)
    {
        if (webSocket.State != WebSocketState.Open)
            return;

        if (!_sendLocks.TryGetValue(webSocket, out var sendLock))
            return;

        try
        {
            await sendLock.WaitAsync();
            try
            {
                await webSocket.SendAsync(data, WebSocketMessageType.Binary, true, CancellationToken.None);
            }
            finally
            {
                sendLock.Release();
            }
        }
        catch (WebSocketException)
        {
            _ = RemoveClientAsync(webSocket);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DataStream: Error sending binary to client");
        }
    }

    /// <summary>
    /// Send a message to a specific WebSocket client.
    /// </summary>
    private async Task SendMessageAsync(WebSocket webSocket, WebSocketMessage message)
    {
        if (webSocket.State != WebSocketState.Open)
            return;

        if (!_sendLocks.TryGetValue(webSocket, out var sendLock))
            return;

        try
        {
            await sendLock.WaitAsync();
            try
            {
                var json = JsonSerializer.Serialize(message);
                var bytes = Encoding.UTF8.GetBytes(json);
                await webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            finally
            {
                sendLock.Release();
            }
        }
        catch (WebSocketException)
        {
            // Client disconnected
            _ = RemoveClientAsync(webSocket);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DataStream: Error sending message to client");
        }
    }

    /// <summary>
    /// Get the number of connected clients.
    /// </summary>
    public int ClientCount => _clients.Count;
}

/// <summary>
/// Information about a connected data stream client.
/// </summary>
public class DataStreamClient
{
    public DateTime ConnectedAt { get; set; }
    public string RemoteEndpoint { get; set; } = string.Empty;
}

/// <summary>
/// Interface for the data streaming server.
/// </summary>
public interface IDataStreamServer
{
    bool IsStreamingActive { get; }
    int ClientCount { get; }

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);

    void EnableStreaming();
    void DisableStreaming();

    Task BroadcastToChannelSubscribersAsync(int channelId, WebSocketMessage message);
    Task BroadcastBinaryToChannelSubscribersAsync(int channelId, byte[] data);
}
