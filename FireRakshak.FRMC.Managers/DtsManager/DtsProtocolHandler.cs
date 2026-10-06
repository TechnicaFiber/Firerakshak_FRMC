using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using FireRakshak.FRMC.Core.Models;
using System.Diagnostics;

namespace FireRakshak.FRMC.Managers.DtsManager;

/// <summary>
/// Handles the low-level communication protocol with the DTS (Distributed Temperature Sensing) device.
/// This class is responsible for sending commands and receiving responses, abstracting the raw byte-level communication.
/// </summary>
public class DtsProtocolHandler : IDisposable
{
    private readonly ILogger<DtsProtocolHandler> _logger;
    private readonly DtsOptions _dtsOptions;
    private TcpClient _tcpClient;
    private NetworkStream? _stream;
    private readonly SemaphoreSlim _commandQueueSemaphore = new SemaphoreSlim(1, 1);
    private readonly int _commandDelayMs; // Configurable delay between commands
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private volatile bool _isConnecting;


    /// <summary>
    /// Gets whether the handler is currently connected to DTS
    /// </summary>
    public bool IsConnected => _tcpClient != null &&
                                _tcpClient.Connected &&
                                _stream != null &&
                                _stream.CanRead &&
                                _stream.CanWrite;

    /// <summary>
    /// Initializes a new instance of the <see cref="DtsProtocolHandler"/> class.
    /// </summary>
    /// <param name="logger">The logger for recording detailed communication information.</param>
    /// <param name="dtsOptions">The DTS configuration options.</param>
    public DtsProtocolHandler(ILogger<DtsProtocolHandler> logger, IOptions<DtsOptions> dtsOptions)
    {
        _logger = logger;
        _dtsOptions = dtsOptions.Value;
        _tcpClient = new TcpClient();
        _commandDelayMs = _dtsOptions.CommandDelayMs > 0 ? _dtsOptions.CommandDelayMs : 50;
        _logger.LogInformation("DtsProtocolHandler initialized with command delay: {DelayMs}ms", _commandDelayMs);
    }

    /// <summary>
    /// Establishes a connection to the DTS device with retry logic.
    /// </summary>
    //public async Task ConnectAsync()
    //{
    //    // Check if already connected by testing stream availability
    //    if (_tcpClient != null && _tcpClient.Connected && _stream != null && _stream.CanRead && _stream.CanWrite)
    //    {
    //        _logger.LogDebug("Already connected to DTS.");
    //        return;
    //    }

    //    // Explicitly close and dispose old connection if it exists
    //    try
    //    {
    //        if (_stream != null)
    //        {
    //            _stream.Close();
    //            _stream.Dispose();
    //            _stream = null;
    //        }

    //        if (_tcpClient != null)
    //        {
    //            _tcpClient.Close();
    //            _tcpClient.Dispose();
    //        }
    //    }
    //    catch (Exception ex)
    //    {
    //        _logger.LogWarning(ex, "Error disposing old TCP connection");
    //    }

    //    // Small delay to ensure socket is fully released
    //    await Task.Delay(50);

    //    // Create new TcpClient
    //    _tcpClient = new TcpClient();

    //    var stopwatch = Stopwatch.StartNew();
    //    var retryInterval = TimeSpan.FromSeconds(5);

    //    while (stopwatch.Elapsed < _dtsOptions.ConnectionRetryPeriod)
    //    {
    //        try
    //        {
    //            _logger.LogInformation("Attempting to connect to DTS at {IpAddress}:{Port} (Time elapsed: {Elapsed})...", _dtsOptions.IpAddress, _dtsOptions.Port, stopwatch.Elapsed);
    //            await _tcpClient.ConnectAsync(_dtsOptions.IpAddress, _dtsOptions.Port);
    //            _stream = _tcpClient.GetStream();
    //            _logger.LogInformation("Successfully connected to DTS.");
    //            return; // Connection successful
    //        }
    //        catch (SocketException ex)
    //        {
    //            _logger.LogWarning(ex, "Failed to connect to DTS. Retrying in {RetryInterval}.", retryInterval);
    //            await Task.Delay(retryInterval);
    //        }
    //        catch (Exception ex)
    //        {
    //            _logger.LogError(ex, "An unexpected error occurred during DTS connection attempt.");
    //            throw; // Rethrow other unexpected exceptions immediately
    //        }
    //    }

    //    _logger.LogError("Failed to connect to DTS after {RetryPeriod}. Giving up.", _dtsOptions.ConnectionRetryPeriod);
    //    throw new TimeoutException($"Failed to connect to DTS after {_dtsOptions.ConnectionRetryPeriod}.");
    //}
    public async Task ConnectAsync()
    {
        await _connectLock.WaitAsync();
        try
        {
            if (_isConnecting)
            {
                _logger.LogDebug("Connection already in progress.");
                return;
            }

            if (_tcpClient?.Connected == true && _stream != null)
            {
                _logger.LogDebug("Already connected to DTS.");
                return;
            }

            _isConnecting = true;

            _stream?.Dispose();
            _tcpClient?.Close();
            _tcpClient?.Dispose();

            _tcpClient = new TcpClient();

            var stopwatch = Stopwatch.StartNew();
            var retryInterval = TimeSpan.FromSeconds(5);

            while (stopwatch.Elapsed < _dtsOptions.ConnectionRetryPeriod)
            {
                try
                {
                    _logger.LogInformation(
                        "Attempting to connect to DTS at {Ip}:{Port} (Elapsed: {Elapsed})",
                        _dtsOptions.IpAddress,
                        _dtsOptions.Port,
                        stopwatch.Elapsed);

                    await _tcpClient.ConnectAsync(
                        _dtsOptions.IpAddress,
                        _dtsOptions.Port);

                    _stream = _tcpClient.GetStream();
                    _logger.LogInformation("Successfully connected to DTS.");
                    return;
                }
                catch (SocketException ex)
                {
                    _logger.LogWarning(ex, "Failed to connect to DTS. Retrying...");
                    _tcpClient.Dispose();
                    _tcpClient = new TcpClient(); // ⚠️ MUST recreate
                    await Task.Delay(retryInterval);
                }
            }

            throw new TimeoutException("Failed to connect to DTS.");
        }
        finally
        {
            _isConnecting = false;
            _connectLock.Release();
        }
    }


    public async Task<byte[]> SendCommandAndReceiveResponseAsync(byte[] command)
    {
        // Wait for exclusive access to send command (queuing)
        await _commandQueueSemaphore.WaitAsync();

        try
        {
            _logger.LogTrace("Sending DTS command ({Length} bytes)", command.Length);

            // Check connection state - throw if not connected (let caller handle reconnection)
            if (!IsConnected)
            {
                _logger.LogError("DTS is not connected. Cannot send command.");
                throw new InvalidOperationException("DTS is not connected. Please ensure connection is established before sending commands.");
            }

            if (_stream == null)
            {
                _logger.LogError("Network stream is not available.");
                throw new InvalidOperationException("Network stream is not available.");
            }

            await _stream.WriteAsync(command, 0, command.Length);
            await _stream.FlushAsync();

            var headerBuffer = new byte[6];
            await _stream.ReadExactlyAsync(headerBuffer, 0, headerBuffer.Length);

            // The length is a 4-byte integer starting at index 2. Network protocols are typically big-endian.
            var lengthBytes = new byte[4];
            Array.Copy(headerBuffer, 2, lengthBytes, 0, 4);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(lengthBytes);
            }
            int responseLength = BitConverter.ToInt32(lengthBytes, 0);
            int totalLength = 6 + responseLength;

            var responseBuffer = new byte[totalLength];
            Array.Copy(headerBuffer, responseBuffer, headerBuffer.Length);

            int bytesRead = headerBuffer.Length;
            if (bytesRead < totalLength)
            {
                await _stream.ReadExactlyAsync(responseBuffer, bytesRead, totalLength - bytesRead);
            }

            _logger.LogTrace("DTS response received ({Length} bytes)", responseBuffer.Length);

            // Configurable delay before allowing next command
            await Task.Delay(_commandDelayMs);

            return responseBuffer;
        }
        finally
        {
            // Release semaphore to allow next command
            _commandQueueSemaphore.Release();
        }
    }

    /// <summary>
    /// Disconnects from the DTS device.
    /// </summary>
    public void Disconnect()
    {
        _logger.LogInformation("Disconnecting from DTS.");
        _stream?.Close();
        _tcpClient?.Close();
    }

    /// <summary>
    /// Updates the connection configuration details for the DTS in-memory.
    /// </summary>
    public void UpdateDtsConnection(string ip, int port)
    {
        _dtsOptions.IpAddress = ip;
        _dtsOptions.Port = port;
        _logger.LogInformation("Updated DTS options in-memory. IP = {IP}, Port = {Port}", ip, port);
        Disconnect();
    }

    public void Dispose()
    {
        Disconnect();
        _stream?.Dispose();
        _tcpClient?.Dispose();
        _commandQueueSemaphore?.Dispose();
    }
}
//using System;
//using System.Net.Sockets;
//using System.Threading;
//using System.Threading.Tasks;
//using Microsoft.Extensions.Logging;
//using Microsoft.Extensions.Options;
//using FireRakshak.FRMC.Core.Models;
//using System.Diagnostics;

//namespace FireRakshak.FRMC.Managers.DtsManager;

///// <summary>
///// Handles the low-level communication protocol with the DTS (Distributed Temperature Sensing) device.
///// This class is responsible for sending commands and receiving responses, abstracting the raw byte-level communication.
///// </summary>
//public class DtsProtocolHandler : IDisposable
//{
//    private readonly ILogger<DtsProtocolHandler> _logger;
//    private readonly DtsOptions _dtsOptions;
//    private TcpClient _tcpClient;
//    private NetworkStream? _stream;
//    private readonly SemaphoreSlim _commandQueueSemaphore = new SemaphoreSlim(1, 1);
//    private const int CommandDelayMilliseconds = 100; // 100ms between commands (reduced for simulator testing)
//    //private const int CommandDelayMilliseconds = 100; // 100ms between commands (reduced for simulator testing)
//    private readonly SemaphoreSlim _connectLock = new SemaphoreSlim(1, 1);

//    /// <summary>
//    /// Gets whether the handler is currently connected to DTS
//    /// </summary>
//    public bool IsConnected => _tcpClient != null &&
//                                _tcpClient.Connected &&
//                                _stream != null &&
//                                _stream.CanRead &&
//                                _stream.CanWrite;

//    /// <summary>
//    /// Initializes a new instance of the <see cref="DtsProtocolHandler"/> class.
//    /// </summary>
//    /// <param name="logger">The logger for recording detailed communication information.</param>
//    /// <param name="dtsOptions">The DTS configuration options.</param>
//    public DtsProtocolHandler(ILogger<DtsProtocolHandler> logger, IOptions<DtsOptions> dtsOptions)
//    {
//        _logger = logger;
//        _dtsOptions = dtsOptions.Value;
//        _tcpClient = new TcpClient();
//    }

//    /// <summary>
//    /// Establishes a connection to the DTS device with retry logic.
//    /// </summary>
//    public async Task ConnectAsync()
//    {
//        await _connectLock.WaitAsync();
//        try
//        {
//            if (_tcpClient != null &&
//                _tcpClient.Connected &&
//                _stream != null &&
//                _stream.CanRead &&
//                _stream.CanWrite)
//            {
//                _logger.LogDebug("Already connected to DTS.");
//                return;
//            }

//            try
//            {
//                _stream?.Close();
//                _stream?.Dispose();
//                _stream = null;

//                _tcpClient?.Close();
//                _tcpClient?.Dispose();
//                _tcpClient = null;
//            }
//            catch (Exception ex)
//            {
//                _logger.LogWarning(ex, "Error disposing old TCP connection");
//            }

//            await Task.Delay(50);

//            var stopwatch = Stopwatch.StartNew();
//            var retryInterval = TimeSpan.FromSeconds(5);

//            while (stopwatch.Elapsed < _dtsOptions.ConnectionRetryPeriod)
//            {
//                try
//                {
//                    _tcpClient = new TcpClient();

//                    _logger.LogInformation(
//                        "Attempting to connect to DTS at {IpAddress}:{Port} (Elapsed: {Elapsed})",
//                        _dtsOptions.IpAddress,
//                        _dtsOptions.Port,
//                        stopwatch.Elapsed);

//                    await _tcpClient.ConnectAsync(_dtsOptions.IpAddress, _dtsOptions.Port);

//                    if (!_tcpClient.Connected)
//                        throw new InvalidOperationException("TCP connect returned but socket is not connected");

//                    _stream = _tcpClient.GetStream();

//                    _logger.LogInformation("Successfully connected to DTS.");
//                    return;
//                }
//                //catch (SocketException ex)
//                //{
//                //    _logger.LogWarning(ex, "Failed to connect to DTS. Retrying in {RetryInterval}", retryInterval);
//                //    await Task.Delay(retryInterval);
//                //}
//                catch (SocketException ex)
//                {
//                    _logger.LogWarning("DTS connection failed — marking DTS as NOT responsive");



//                    throw; // optional, based on your retry logic
//                }

//            }

//            throw new TimeoutException(
//                $"Failed to connect to DTS after {_dtsOptions.ConnectionRetryPeriod}");
//        }
//        finally
//        {
//            _connectLock.Release();
//        }
//    }

//    public async Task<byte[]> SendCommandAndReceiveResponseAsync(byte[] command)
//    {
//        // Wait for exclusive access to send command (queuing)
//        await _commandQueueSemaphore.WaitAsync();

//        try
//        {
//            _logger.LogDebug("Sending command to DTS: {Command}", BitConverter.ToString(command));

//            // Check connection state - throw if not connected (let caller handle reconnection)
//            if (!IsConnected)
//            {
//                _logger.LogError("DTS is not connected. Cannot send command.");
//                throw new InvalidOperationException("DTS is not connected. Please ensure connection is established before sending commands.");
//            }

//            if (_stream == null)
//            {
//                _logger.LogError("Network stream is not available.");
//                throw new InvalidOperationException("Network stream is not available.");
//            }

//            await _stream.WriteAsync(command, 0, command.Length);
//            await _stream.FlushAsync();

//            var headerBuffer = new byte[6];
//            await _stream.ReadExactlyAsync(headerBuffer, 0, headerBuffer.Length);

//            // The length is a 4-byte integer starting at index 2. Network protocols are typically big-endian.
//            var lengthBytes = new byte[4];
//            Array.Copy(headerBuffer, 2, lengthBytes, 0, 4);
//            if (BitConverter.IsLittleEndian)
//            {
//                Array.Reverse(lengthBytes);
//            }
//            int responseLength = BitConverter.ToInt32(lengthBytes, 0);
//            int totalLength = 6 + responseLength;

//            var responseBuffer = new byte[totalLength];
//            Array.Copy(headerBuffer, responseBuffer, headerBuffer.Length);

//            int bytesRead = headerBuffer.Length;
//            if (bytesRead < totalLength)
//            {
//                await _stream.ReadExactlyAsync(responseBuffer, bytesRead, totalLength - bytesRead);
//            }

//            _logger.LogDebug("Received response from DTS: {Response}", BitConverter.ToString(responseBuffer));

//            // Add 2-second delay before allowing next command
//            await Task.Delay(CommandDelayMilliseconds);

//            return responseBuffer;
//        }
//        finally
//        {
//            // Release semaphore to allow next command
//            _commandQueueSemaphore.Release();
//        }
//    }

//    /// <summary>
//    /// Disconnects from the DTS device.
//    /// </summary>
//    public void Disconnect()
//    {
//        _logger.LogInformation("Disconnecting from DTS.");
//        _stream?.Close();
//        _tcpClient?.Close();
//    }

//    public void Dispose()
//    {
//        Disconnect();
//        _stream?.Dispose();
//        _tcpClient?.Dispose();
//        _commandQueueSemaphore?.Dispose();
//    }
//}