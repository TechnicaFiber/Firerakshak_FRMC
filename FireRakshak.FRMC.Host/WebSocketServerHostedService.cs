//using FireRakshak.FRMC.Core.Enums;
//using FireRakshak.FRMC.Core.EventAggregator;      
//using FireRakshak.FRMC.Core.Events;
//using FireRakshak.FRMC.Core.Models;
//using Microsoft.Extensions.Configuration;          
//using Microsoft.Extensions.Hosting;
//using Microsoft.Extensions.Logging;
//using System;
//using System.Collections.Generic;
//using System.Linq;
//using System.Net;
//using System.Net.Sockets;                          
//using System.Net.WebSockets;
//using System.Text;
//using System.Text.Json;
//using System.Threading;
//using System.Threading.Tasks;
//namespace FireRakshak.FRMC.Host
//{
//    public class WebSocketServerHostedService : IHostedService
//    {
//        private readonly ILogger<WebSocketServerHostedService> _logger;
//        private readonly IServiceProvider _serviceProvider;
//        private readonly IEventAggregator _eventAggregator; 
//        private readonly IConfiguration _config;            
//        private FrmcConfiguration _frmcConfig;
//        private HttpListener? _wsListener;
//        private HttpListener? _wssListener;
//        private CancellationTokenSource? _cts;
//        private WebSocket? _connectedClientSocket;
//        public WebSocketServerHostedService(
//            ILogger<WebSocketServerHostedService> logger,
//            IServiceProvider serviceProvider,
//            FrmcConfiguration frmcConfig,
//            IEventAggregator eventAggregator, 
//            IConfiguration config              
//        )
//        {
//            _logger = logger;
//            _serviceProvider = serviceProvider;
//            _frmcConfig = frmcConfig ?? FrmcConfiguration.LoadFromFile("config.json");
//            _eventAggregator = eventAggregator; 
//            _config = config;                   
//        }
//        public Task StartAsync(CancellationToken cancellationToken)
//        {
//            _logger.LogInformation("WebSocketServerHostedService is starting.");
//            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

//            try
//            {
//                _wsListener = new HttpListener();
//                _wsListener.Prefixes.Add("http://localhost:5000/general_ws/");
//                _wsListener.Start();
//                _logger.LogInformation("WS Listener started on {Prefix}", "http://localhost:5000/general_ws/");
//                _ = Task.Run(() => StartListening(_wsListener, _cts.Token));
//            }
//            catch (HttpListenerException ex)
//            {
//                _logger.LogError(ex, "Failed to start WS Listener");
//            }
//            try
//            {
//                _wssListener = new HttpListener();
//                _wssListener.Prefixes.Add("https://localhost:5001/service_ws/");
//                _wssListener.Start();
//                _logger.LogInformation("WSS Listener started on {Prefix}", "https://localhost:5001/service_ws/");
//                _ = Task.Run(() => StartListening(_wssListener, _cts.Token));
//            }
//            catch (HttpListenerException ex)
//            {
//                _logger.LogError(ex, "Failed to start WSS Listener");
//            }
            
//            _ = Task.Run(async () =>
//            {
//                string dtsHost = _config["Dts:IpAddress"] ?? "127.0.0.1";
//                int dtsPort = int.TryParse(_config["Dts:Port"], out var p) ? p : 3000;

//                Console.WriteLine($"[DTS] Using endpoint {dtsHost}:{dtsPort} (from appsettings.json)");
//                int currentChannel = 0; 
//                while (!_cts!.IsCancellationRequested)
//                {
//                    try
//                    {
//                        int ch = currentChannel;

//                        using var client = new TcpClient();
//                        await client.ConnectAsync(dtsHost, dtsPort);
//                        using var stream = client.GetStream();

//                        byte[] request = new byte[] { 0xCC, 0x55, 0x00, 0x00, 0x00, 0x00, 0x00, 0x07, (byte)ch };
//                        await stream.WriteAsync(request, 0, request.Length, _cts.Token);

//                        byte[] buffer = new byte[64 * 1024];
//                        int len = await stream.ReadAsync(buffer, 0, buffer.Length, _cts.Token);

//                        if (len > 0)
//                        {
//                            Console.WriteLine($"[DTS] CH {ch} RAW: " + BitConverter.ToString(buffer, 0, len));
//                            int dataStart = 13;
//                            int dataEnd = Math.Max(dataStart, len - 2);
//                            int pointCount = (dataEnd - dataStart) / 2;
//                            var temps = new TemperatureDataPoint[pointCount];
//                            for (int i = 0; i < pointCount; i++)
//                            {
//                                int offset = dataStart + i * 2;
//                                short raw = (short)((buffer[offset] << 8) | buffer[offset + 1]);
//                                double t = raw / 10.0;
//                                temps[i] = new TemperatureDataPoint(t, i, DateTime.Now);
//                            }
//                            Console.WriteLine($"[DTS] CH {ch} Temps: " + string.Join(", ", temps.Select(x => x.Temperature.ToString("0.0"))));
//                            await _eventAggregator.PublishAsync(new TemperatureDataReceivedEvent(ch, temps, DateTime.Now));
//                            // === send real-time temperature to DTSCM ===
//                            try
//                            {
//                                if (_connectedClientSocket != null && _connectedClientSocket.State == WebSocketState.Open)
//                                {
//                                    var jsonObj = new
//                                    {
//                                        MessageType = "live_temperature_data",
//                                        Payload = new
//                                        {
//                                            ZoneId = ch,
//                                            Data = temps
//                                        }
//                                    };
//                                    string json = JsonSerializer.Serialize(jsonObj);
//                                    var bytes = Encoding.UTF8.GetBytes(json);
//                                    await _connectedClientSocket.SendAsync(
//                                        new ArraySegment<byte>(bytes),
//                                        WebSocketMessageType.Text,
//                                        true,
//                                        CancellationToken.None);

//                                    Console.WriteLine($"[WS->DTSCM] sent CH{ch} temperature packet");
//                                }
//                            }
//                            catch (Exception ex)
//                            {
//                                Console.WriteLine("[WS->DTSCM] send failed: " + ex.Message);
//                            }

//                        }
//                    }
//                    catch (Exception ex)
//                    {
//                        Console.WriteLine("[DTS] Poll error: " + ex.Message);
//                    }
//                    currentChannel++;
//                    if (currentChannel > 7)
//                        currentChannel = 0;

//                    try { await Task.Delay(TimeSpan.FromSeconds(6), _cts.Token); }
//                    catch (TaskCanceledException) { }
//                }
//            });
//            return Task.CompletedTask;
//        }
//        public async Task StopAsync(CancellationToken cancellationToken)
//        {
//            _logger.LogInformation("WebSocketServerHostedService is stopping.");
//            _cts?.Cancel();
//            _cts?.Dispose();
//            if (_wsListener?.IsListening == true)
//            {
//                _wsListener.Stop();
//                _wsListener.Close();
//                _logger.LogInformation("WS Listener stopped.");
//            }
//            if (_wssListener?.IsListening == true)
//            {
//                _wssListener.Stop();
//                _wssListener.Close();
//                _logger.LogInformation("WSS Listener stopped.");
//            }
//            await Task.CompletedTask;
//        }
//        private async Task StartListening(HttpListener listener, CancellationToken cancellationToken)
//        {
//            while (!cancellationToken.IsCancellationRequested)
//            {
//                try
//                {
//                    var context = await listener.GetContextAsync();
//                    if (context.Request.IsWebSocketRequest)
//                    {
//                        _ = HandleWebSocketConnection(context, cancellationToken);
//                    }
//                    else
//                    {
//                        context.Response.StatusCode = 400;
//                        context.Response.Close();
//                    }
//                }
//                catch (HttpListenerException ex)
//                {
//                    _logger.LogInformation("HttpListener stopped: {Message}", ex.Message);
//                    break;
//                }
//                catch (Exception ex)
//                {
//                    _logger.LogError(ex, "Error in HttpListener.StartListening");
//                }
//            }
//        }
//        private async Task HandleWebSocketConnection(HttpListenerContext httpListenerContext, CancellationToken cancellationToken)
//        {
//            _logger.LogInformation("Incoming WebSocket connection request.");
//            HttpListenerWebSocketContext? webSocketContext = null;
//            try
//            {
//                webSocketContext = await httpListenerContext.AcceptWebSocketAsync(null);
//                _logger.LogInformation("WebSocket connection accepted.");

//                var webSocket = webSocketContext.WebSocket;
//                _connectedClientSocket = webSocket;

//                var buffer = new byte[1024 * 4];

//                while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
//                {
//                    WebSocketReceiveResult result;
//                    try
//                    {
//                        result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
//                    }
//                    catch (WebSocketException wsEx)
//                    {
//                        _logger.LogWarning("WebSocket disconnected: {Message}", wsEx.Message);
//                        break;
//                    }

//                    if (result.MessageType == WebSocketMessageType.Close)
//                    {
//                        _logger.LogInformation("WebSocket connection closed by client.");
//                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cancellationToken);
//                        break;
//                    }
//                    var messageJson = Encoding.UTF8.GetString(buffer, 0, result.Count);
//                    _logger.LogInformation("Received WebSocket message: {Message}", messageJson);

//                    WebSocketMessage? message = null;
//                    try
//                    {
//                        message = JsonSerializer.Deserialize<WebSocketMessage>(messageJson);
//                        if (message?.Payload.ValueKind != JsonValueKind.Undefined)
//                        {
//                            // Clone payload to prevent disposal issues
//                            message.Payload = message.Payload.Clone();
//                        }
//                    }
//                    catch (Exception ex)
//                    {
//                        _logger.LogError(ex, "Failed to deserialize incoming WebSocket message.");
//                    }

//                    if (message != null)
//                    {
//                        await HandleWebSocketMessage(message, webSocket, cancellationToken);
//                    }
//                }
//            }
//            catch (Exception ex)
//            {
//                _logger.LogError(ex, "Error handling WebSocket connection");
//                httpListenerContext.Response.StatusCode = 500;
//                httpListenerContext.Response.Close();
//            }
//            finally
//            {
//                webSocketContext?.WebSocket.Dispose();
//            }
//        }
//        private async Task HandleWebSocketMessage(WebSocketMessage message, WebSocket webSocket, CancellationToken cancellationToken)
//        {
//            switch (message.MessageType)
//            {
//                case "GetFRMCIP":
//                    if (_frmcConfig == null)
//                    {
//                        _logger.LogWarning("FRMC configuration is not initialized.");
//                        await SendResponse(webSocket, new WebSocketMessage
//                        {
//                            MessageType = "GetFRMCIP_response",
//                            Payload = JsonSerializer.SerializeToElement(new
//                            {
//                                Success = false,
//                                Error = "FRMC configuration not available"
//                            })
//                        }, cancellationToken);
//                        break;
//                    }
//                    var currentIP = _frmcConfig.CurrentIP;
//                    _logger.LogInformation("FRMC IP requested. Current IP: {CurrentIP}", currentIP);
//                    await SendResponse(webSocket, new WebSocketMessage
//                    {
//                        MessageType = "GetFRMCIP_response",
//                        Payload = JsonSerializer.SerializeToElement(new
//                        {
//                            Success = true,
//                            CurrentIP = currentIP
//                        })
//                    }, cancellationToken);
//                    break;
//                case "Hi":
//                    _logger.LogInformation("Received 'Hi' message from client.");
//                    await SendResponse(webSocket, new WebSocketMessage
//                    {
//                        MessageType = "Hi_response",
//                        Payload = JsonSerializer.SerializeToElement(new
//                        {
//                            Acknowledged = true,
//                            ServerMessage = "Hello from FRMC!"
//                        })
//                    }, cancellationToken);
//                    break;

//                case "SetFRMCIP":
//                    string? newIP = null;
//                    try
//                    {
//                        newIP = message.Payload.GetProperty("NewIP").GetString();
//                    }
//                    catch (Exception ex)
//                    {
//                        _logger.LogError(ex, "Error reading 'NewIP' property from payload.");
//                    }
//                    if (!string.IsNullOrEmpty(newIP) && IPAddress.TryParse(newIP, out _))
//                    {
//                        _frmcConfig.IpAddress = newIP;
//                        _frmcConfig.SaveToFile("config.json");

//                        var changes = new Dictionary<string, object> { { "FRMC_IP", newIP } };
//                        //var evt = new ConfigurationChangedEvent(ConfigurationType.Network, "DTSCM", "FRMC, changes);
//                        var evt = new ConfigurationChangedEvent(ConfigurationType.Network, "DTSCM", "FRMC", changes);


//                        _logger.LogInformation("ConfigurationChanged triggered: {Type} from {Source} with changes: {Changes}",
//                            evt.ConfigType, evt.Source, evt.Changes);

//                        _logger.LogInformation("FRMC IP updated successfully to {NewIP}", newIP);

//                        await SendResponse(webSocket, new WebSocketMessage
//                        {
//                            MessageType = "SetFRMCIP_response",
//                            Payload = JsonSerializer.SerializeToElement(new
//                            {
//                                Success = true,
//                                Message = "IP updated successfully"
//                            })
//                        }, cancellationToken);
//                    }
//                    else
//                    {
//                        _logger.LogWarning("Invalid IP address received: {NewIP}", newIP);
//                        await SendResponse(webSocket, new WebSocketMessage
//                        {
//                            MessageType = "SetFRMCIP_response",
//                            Payload = JsonSerializer.SerializeToElement(new
//                            {
//                                Success = false,
//                                Message = "Invalid IP address"
//                            })
//                        }, cancellationToken);
//                    }
//                    break;

//                case "GetFRMCTime":
//                    var currentTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
//                    _logger.LogInformation("FRMC time requested. Current time: {Time}", currentTime);
//                    await SendResponse(webSocket, new WebSocketMessage
//                    {
//                        MessageType = "GetFRMCTime_response",
//                        Payload = JsonSerializer.SerializeToElement(new
//                        {
//                            CurrentTime = currentTime
//                        })
//                    }, cancellationToken);
//                    break;

//                case "SetFRMCTime":
//                    string? newTimeStr = null;
//                    try
//                    {
//                        if (message.Payload.TryGetProperty("DeviceTime", out var deviceTimeProp))
//                            newTimeStr = deviceTimeProp.GetString();
//                        else if (message.Payload.TryGetProperty("NewTime", out var newTimeProp))
//                            newTimeStr = newTimeProp.GetString();
//                    }
//                    catch (Exception ex)
//                    {
//                        _logger.LogError(ex, "Error reading time property from payload.");
//                    }

//                    if (string.IsNullOrEmpty(newTimeStr))
//                    {
//                        _logger.LogWarning("SetFRMCTime message missing time field (DeviceTime/NewTime).");
//                        await SendResponse(webSocket, new WebSocketMessage
//                        {
//                            MessageType = "SetFRMCTime_response",
//                            Payload = JsonSerializer.SerializeToElement(new
//                            {
//                                Success = false,
//                                Message = "Payload missing DeviceTime or NewTime"
//                            })
//                        }, cancellationToken);
//                        break;
//                    }

//                    if (DateTime.TryParse(newTimeStr, out var newTime))
//                    {
//                        _logger.LogInformation("FRMC time updated successfully to: {NewTime}", newTime.ToString("yyyy-MM-dd HH:mm:ss"));
//                        await SendResponse(webSocket, new WebSocketMessage
//                        {
//                            MessageType = "SetFRMCTime_response",
//                            Payload = JsonSerializer.SerializeToElement(new
//                            {
//                                Success = true,
//                                Message = $"Time updated to {newTime:yyyy-MM-dd HH:mm:ss}"
//                            })
//                        }, cancellationToken);
//                    }
//                    else
//                    {
//                        _logger.LogWarning("Invalid time format received: {NewTime}", newTimeStr);
//                        await SendResponse(webSocket, new WebSocketMessage
//                        {
//                            MessageType = "SetFRMCTime_response",
//                            Payload = JsonSerializer.SerializeToElement(new
//                            {
//                                Success = false,
//                                Message = "Invalid time format"
//                            })
//                        }, cancellationToken);
//                    }
//                    break;
//            }
//        }
//        private async Task SendResponse(WebSocket webSocket, WebSocketMessage response, CancellationToken cancellationToken)
//        {
//            var json = JsonSerializer.Serialize(response);
//            var bytes = Encoding.UTF8.GetBytes(json);
//            await webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
//        }
//    }
//}
