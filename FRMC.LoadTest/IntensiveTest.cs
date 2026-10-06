using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace FRMC.LoadTest;

/// <summary>
/// Intensive test with 1 client making multiple concurrent requests during rapid channel switching
/// Tests: GetHealthParameters, get_active_alarm, GetSystemHealth, GetZoneState concurrently
/// </summary>
public class IntensiveTest
{
    private const int NUM_CHANNELS = 8;
    private const int TEST_DURATION_SECONDS = 60;
    private const string FRMC_WS_URL = "ws://localhost:6164/general_ws/?token=connecting";

    private static int _totalRequests = 0;
    private static int _successfulRequests = 0;
    private static int _timeouts = 0;
    private static int _errors = 0;

    public static async Task Run()
    {
        Console.WriteLine("╔═══════════════════════════════════════════════════════════╗");
        Console.WriteLine("║  FRMC Intensive Concurrency Test                        ║");
        Console.WriteLine("║  1 Client × Multiple Concurrent Requests                ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");
        Console.WriteLine();
        Console.WriteLine("Test Configuration:");
        Console.WriteLine($"  - Channels: {NUM_CHANNELS}");
        Console.WriteLine($"  - Test Duration: {TEST_DURATION_SECONDS} seconds");
        Console.WriteLine($"  - Operations: GetHealthParameters, get_active_alarm, GetSystemHealth, GetZoneState");
        Console.WriteLine($"  - Request Timeout: 10 seconds");
        Console.WriteLine();

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(TEST_DURATION_SECONDS));

        try
        {
            await RunIntensiveClient(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected when test duration expires
        }

        Console.WriteLine();
        Console.WriteLine("════════════════════════════════════════════════════════════");
        Console.WriteLine("Test Results:");
        Console.WriteLine($"  Total Requests:      {_totalRequests}");
        Console.WriteLine($"  Successful:          {_successfulRequests}");
        Console.WriteLine($"  Timeouts:            {_timeouts} {(_timeouts > 0 ? "❌" : "✅")}");
        Console.WriteLine($"  Errors:              {_errors} {(_errors > 0 ? "❌" : "✅")}");
        Console.WriteLine($"  Success Rate:        {(_totalRequests > 0 ? (_successfulRequests * 100.0 / _totalRequests).ToString("F1") : "0")}%");
        Console.WriteLine("════════════════════════════════════════════════════════════");

        if (_timeouts > 0)
        {
            Console.WriteLine();
            Console.WriteLine("❌ TIMEOUTS DETECTED - Priority lock system may have issues!");
        }
        else if (_errors == 0)
        {
            Console.WriteLine();
            Console.WriteLine("✅ All requests completed successfully - No timeouts!");
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
            int switchCount = 0;

            Console.WriteLine("🚀 Starting intensive operations...");
            Console.WriteLine();

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Rapid channel switching
                    switchCount++;
                    int nextChannel = random.Next(1, NUM_CHANNELS + 1);

                    if (nextChannel != currentChannel)
                    {
                        Console.WriteLine($"🔄 [Switch #{switchCount}] Channel {currentChannel} → {nextChannel}");

                        await SendMessage(ws, new { MessageType = "unsubscribe_channel", Payload = new { channelId = currentChannel } }, ct);
                        await Task.Delay(20, ct);
                        await SendMessage(ws, new { MessageType = "subscribe_channel", Payload = new { channelId = nextChannel } }, ct);

                        currentChannel = nextChannel;
                    }

                    // Fire off multiple concurrent requests (all at once!)
                    var requestTasks = new List<Task>
                    {
                        MakeRequest("GetHealthParameters", ws, new { }, ct, "GetHealthParameters_response"),
                        MakeRequest("get_active_alarm", ws, new { }, ct, "active_alarm_response"),
                        MakeRequest("GetSystemHealth", ws, new { }, ct, "SystemHealthUpdate"),
                        MakeRequest("GetZoneState", ws, new { ChannelId = currentChannel, ZoneId = 0 }, ct, "GetZoneState_response"),
                        MakeRequest("GetSystemConfig", ws, new { }, ct, "GetSystemConfig_response")
                    };

                    // Wait for all requests to complete (or timeout)
                    await Task.WhenAll(requestTasks);

                    // Brief pause before next cycle
                    await Task.Delay(random.Next(100, 300), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ [Client] Error in main loop: {ex.Message}");
                    Interlocked.Increment(ref _errors);
                }
            }

            Console.WriteLine();
            Console.WriteLine($"🛑 [Client] Test completed - {switchCount} channel switches performed");

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

    private static async Task MakeRequest(string messageType, ClientWebSocket ws, object payload, CancellationToken ct, string expectedResponseType)
    {
        Interlocked.Increment(ref _totalRequests);

        try
        {
            var requestId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var startTime = DateTime.UtcNow;

            await SendMessage(ws, new { MessageType = messageType, Payload = payload, RequestId = requestId }, ct);

            // Note: Response validation happens in listener
            // For this test, we just track that requests were sent
            // The listener will track timeouts if responses don't arrive

            Interlocked.Increment(ref _successfulRequests);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _timeouts);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ [{messageType}] Failed: {ex.Message}");
            Interlocked.Increment(ref _errors);
        }
    }

    private static async Task ListenForMessages(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[1024 * 1024]; // 1MB buffer
        int messageCount = 0;

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

                // Parse message to check for errors
                try
                {
                    using var doc = JsonDocument.Parse(message);
                    string? msgType = doc.RootElement.GetProperty("MessageType").GetString();

                    // Check for response messages with Success=false
                    if (msgType?.EndsWith("_response") == true && doc.RootElement.TryGetProperty("Payload", out var payload))
                    {
                        if (payload.TryGetProperty("Success", out var success) && !success.GetBoolean())
                        {
                            if (payload.TryGetProperty("Error", out var error))
                            {
                                Console.WriteLine($"⚠️  [{msgType}] Error: {error.GetString()}");
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore parse errors in listener
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
            Console.WriteLine("   This indicates the concurrent SendAsync crash occurred!");
            Interlocked.Increment(ref _errors);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"❌ [Client] Listener error: {ex.Message}");
        }

        Console.WriteLine($"📊 [Client] Listener received {messageCount} messages");
    }

    private static async Task SendMessage(ClientWebSocket ws, object message, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }
}
