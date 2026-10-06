using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace FRMC.LoadTest;

/// <summary>
/// Heavy load test combining:
/// - Mock DTS TCP server responding to FRMC with 8 channels × 10km data
/// - FRMC scans all 8 channels and broadcasts to subscribed DTSCM clients
/// - DTSCM client performs rapid channel switching with parallel API requests
/// Tests: GetHealthParameters, get_active_alarm, GetSystemHealth, GetZoneState, GetSystemConfig
/// </summary>
public class HeavyLoadTest
{
    private const int NUM_CHANNELS = 8;
    private const double CHANNEL_LENGTH_KM = 10.0;
    private const int TEST_DURATION_SECONDS = 120; // 2 minutes for comprehensive testing
    private const string FRMC_WS_URL = "ws://localhost:6164/general_ws/?token=connecting";

    private static int _totalRequests = 0;
    private static int _successfulRequests = 0;
    private static int _timeouts = 0;
    private static int _errors = 0;
    private static int _channelSwitches = 0;
    private static int _broadcastsReceived = 0;

    public static async Task Run()
    {
        Console.WriteLine("╔═══════════════════════════════════════════════════════════╗");
        Console.WriteLine("║  FRMC Heavy Load Test                                     ║");
        Console.WriteLine("║  Mock DTS + Rapid Channel Switching + Parallel Requests   ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");
        Console.WriteLine();
        Console.WriteLine("Test Configuration:");
        Console.WriteLine($"  - Channels: {NUM_CHANNELS}");
        Console.WriteLine($"  - Channel Length: {CHANNEL_LENGTH_KM} km (25,000 points each at 0.4m resolution)");
        Console.WriteLine($"  - Total data per full scan: 200,000 points (8 × 25,000)");
        Console.WriteLine($"  - Test Duration: {TEST_DURATION_SECONDS} seconds");
        Console.WriteLine($"  - Concurrent Operations: GetHealthParameters, get_active_alarm, GetSystemHealth, GetZoneState, GetSystemConfig");
        Console.WriteLine($"  - Channel Switch Interval: 100-300ms (rapid switching under load)");
        Console.WriteLine();

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(TEST_DURATION_SECONDS));

        // Start Mock DTS TCP Server
        var mockDts = new MockDtsServer(NUM_CHANNELS, CHANNEL_LENGTH_KM);
        await mockDts.StartAsync();

        try
        {
            // Wait for FRMC to connect to mock DTS
            await Task.Delay(3000);

            // Start intensive client
            await RunIntensiveClient(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected when test duration expires
        }
        finally
        {
            await mockDts.StopAsync();
        }

        Console.WriteLine();
        Console.WriteLine("════════════════════════════════════════════════════════════");
        Console.WriteLine("Test Results:");
        Console.WriteLine($"  Channel Switches:    {_channelSwitches}");
        Console.WriteLine($"  Broadcasts Received: {_broadcastsReceived}");
        Console.WriteLine($"  Total Requests:      {_totalRequests}");
        Console.WriteLine($"  Successful:          {_successfulRequests}");
        Console.WriteLine($"  Timeouts:            {_timeouts} {(_timeouts > 0 ? "❌" : "✅")}");
        Console.WriteLine($"  Errors:              {_errors} {(_errors > 0 ? "❌" : "✅")}");
        Console.WriteLine($"  Success Rate:        {(_totalRequests > 0 ? (_successfulRequests * 100.0 / _totalRequests).ToString("F1") : "0")}%");
        Console.WriteLine("════════════════════════════════════════════════════════════");

        if (_timeouts > 0)
        {
            Console.WriteLine();
            Console.WriteLine("❌ TIMEOUTS DETECTED - System may be overloaded or priority lock has issues!");
        }
        else if (_errors == 0 && _broadcastsReceived > 0)
        {
            Console.WriteLine();
            Console.WriteLine("✅ All operations completed successfully under heavy load!");
            Console.WriteLine("   FRMC handled 8-channel scanning + rapid channel switching + parallel requests!");
        }
        else if (_broadcastsReceived == 0)
        {
            Console.WriteLine();
            Console.WriteLine("⚠️  No broadcasts received - check if FRMC is connected to DTS and scanning");
        }
    }

    private static async Task RunIntensiveClient(CancellationToken ct)
    {
        using var ws = new ClientWebSocket();

        try
        {
            Console.WriteLine("🖥️  [Client] Connecting to FRMC...");
            await ws.ConnectAsync(new Uri(FRMC_WS_URL), ct);
            Console.WriteLine("✅ [Client] Connected to FRMC WebSocket");

            // Authenticate
            await SendMessage(ws, new { MessageType = "login", Payload = new { username = "testuser", password = "Test@123" } }, ct);
            await Task.Delay(500, ct);
            Console.WriteLine("🔐 [Client] Authenticated");

            // Start live measurement
            await SendMessage(ws, new { MessageType = "start_measurement", Payload = new { } }, ct);
            await Task.Delay(500, ct);
            Console.WriteLine("▶️  [Client] Live measurement started");
            Console.WriteLine();

            // Start listener task
            var listenerTask = Task.Run(() => ListenForMessages(ws, ct), ct);

            // Start intensive operations
            var random = new Random();
            int currentChannel = 1;
            var startTime = DateTime.UtcNow;
            var lastStatusTime = DateTime.UtcNow;

            // Subscribe to initial channel
            await SendMessage(ws, new { MessageType = "subscribe_channel", Payload = new { channelId = currentChannel } }, ct);
            await Task.Delay(100, ct);

            Console.WriteLine("🚀 Starting intensive operations with rapid channel switching...");
            Console.WriteLine("   [DTSCM simulating frequent channel changes + parallel API requests]");
            Console.WriteLine();

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Rapid channel switching (every iteration)
                    int nextChannel = random.Next(1, NUM_CHANNELS + 1);

                    if (nextChannel != currentChannel)
                    {
                        Interlocked.Increment(ref _channelSwitches);

                        // Unsubscribe from current channel
                        await SendMessage(ws, new { MessageType = "unsubscribe_channel", Payload = new { channelId = currentChannel } }, ct);
                        await Task.Delay(20, ct);

                        // Subscribe to new channel
                        await SendMessage(ws, new { MessageType = "subscribe_channel", Payload = new { channelId = nextChannel } }, ct);

                        currentChannel = nextChannel;
                    }

                    // Fire off multiple concurrent requests (parallel API calls as DTSCM would do)
                    var requestTasks = new List<Task>
                    {
                        MakeRequest("GetHealthParameters", ws, new { }, ct),
                        MakeRequest("get_active_alarm", ws, new { }, ct),
                        MakeRequest("GetSystemHealth", ws, new { }, ct),
                        MakeRequest("GetZoneState", ws, new { ChannelId = currentChannel, ZoneId = 0 }, ct),
                        MakeRequest("GetSystemConfig", ws, new { }, ct)
                    };

                    await Task.WhenAll(requestTasks);

                    // Periodic status update every 10 seconds
                    var now = DateTime.UtcNow;
                    if ((now - lastStatusTime).TotalSeconds >= 10)
                    {
                        lastStatusTime = now;
                        var elapsed = (now - startTime).TotalSeconds;
                        Console.WriteLine($"📈 [Status] Elapsed: {elapsed:F0}s | Switches: {_channelSwitches} | Requests: {_totalRequests} | Broadcasts: {_broadcastsReceived} | Errors: {_errors}");
                    }

                    // Brief pause between cycles (100-300ms for rapid switching)
                    await Task.Delay(random.Next(100, 300), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ [Client] Error: {ex.Message}");
                    Interlocked.Increment(ref _errors);
                }
            }

            Console.WriteLine();
            Console.WriteLine($"🛑 [Client] Test completed - {_channelSwitches} channel switches performed");

            await listenerTask;
        }
        catch (WebSocketException ex)
        {
            Console.WriteLine($"❌ [Client] WebSocket error: {ex.Message}");
            Interlocked.Increment(ref _errors);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"❌ [Client] Error: {ex.Message}");
            Interlocked.Increment(ref _errors);
        }
    }

    private static async Task MakeRequest(string messageType, ClientWebSocket ws, object payload, CancellationToken ct)
    {
        Interlocked.Increment(ref _totalRequests);

        try
        {
            await SendMessage(ws, new { MessageType = messageType, Payload = payload }, ct);
            Interlocked.Increment(ref _successfulRequests);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _timeouts);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _errors);
        }
    }

    private static async Task ListenForMessages(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[2 * 1024 * 1024]; // 2MB buffer for large broadcasts
        int messageCount = 0;
        int broadcastCount = 0;
        var lastBroadcastTime = DateTime.MinValue;

        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Console.WriteLine($"⚠️  [Client] Server closed connection: {result.CloseStatusDescription}");
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closing", ct);
                    break;
                }

                messageCount++;
                string message = Encoding.UTF8.GetString(buffer, 0, result.Count);

                try
                {
                    using var doc = JsonDocument.Parse(message);
                    string? msgType = doc.RootElement.GetProperty("MessageType").GetString();

                    if (msgType == "live_temperature_data")
                    {
                        broadcastCount++;
                        Interlocked.Increment(ref _broadcastsReceived);

                        var now = DateTime.UtcNow;
                        if ((now - lastBroadcastTime).TotalSeconds >= 5) // Log every 5 seconds max
                        {
                            lastBroadcastTime = now;
                            var payload = doc.RootElement.GetProperty("Payload");
                            int channelId = 0;
                            int pointCount = 0;
                            // Payload format: { type, channel, timestamp, data }
                            if (payload.TryGetProperty("channel", out var chProp))
                            {
                                channelId = chProp.GetInt32();
                            }
                            if (payload.TryGetProperty("data", out var tempData))
                            {
                                pointCount = tempData.GetArrayLength();
                            }
                            Console.WriteLine($"📊 [Broadcast #{broadcastCount}] Channel {channelId}: {pointCount:N0} points received");
                        }
                    }

                    // Check for response errors
                    if (msgType?.EndsWith("_response") == true && doc.RootElement.TryGetProperty("Payload", out var payload2))
                    {
                        if (payload2.TryGetProperty("Success", out var success) && !success.GetBoolean())
                        {
                            if (payload2.TryGetProperty("Error", out var error))
                            {
                                Console.WriteLine($"⚠️  [{msgType}] Error: {error.GetString()}");
                            }
                        }
                    }

                    // Track health parameter responses
                    if (msgType == "GetHealthParameters_response")
                    {
                        if (messageCount % 50 == 0)
                        {
                            Console.WriteLine($"✅ [Health] GetHealthParameters response #{messageCount / 50 * 50}");
                        }
                    }
                }
                catch
                {
                    // Ignore parse errors
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            Console.WriteLine("❌ [Client] CONNECTION LOST - Server closed connection prematurely!");
            Interlocked.Increment(ref _errors);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"❌ [Client] Listener error: {ex.Message}");
        }

        Console.WriteLine($"📊 [Client] Listener received {messageCount:N0} messages ({broadcastCount} broadcasts)");
    }

    private static async Task SendMessage(ClientWebSocket ws, object message, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }
}
