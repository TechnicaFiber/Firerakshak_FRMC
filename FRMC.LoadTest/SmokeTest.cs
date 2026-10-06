using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace FRMC.LoadTest;

/// <summary>
/// Quick smoke test to verify:
/// 1. Mock DTS server can receive FRMC connections
/// 2. FRMC WebSocket connection works
/// 3. Authentication works
/// 4. Channel subscription works
/// 5. Broadcasts are received
/// 6. API requests work (GetHealthParameters, get_active_alarm)
/// </summary>
public class SmokeTest
{
    private const string FRMC_WS_URL = "ws://localhost:6164/general_ws/?token=connecting";
    private const int TIMEOUT_SECONDS = 30;

    public static async Task Run()
    {
        Console.WriteLine("╔═══════════════════════════════════════════════════════════╗");
        Console.WriteLine("║  FRMC Smoke Test - Verifying All Connections              ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        var results = new Dictionary<string, (bool success, string message)>();

        // Test 1: Start Mock DTS Server
        Console.WriteLine("1️⃣  Starting Mock DTS Server on port 3001...");
        var mockDts = new MockDtsServer(8, 10.0);
        try
        {
            await mockDts.StartAsync();
            results["Mock DTS Server"] = (true, "Started on port 3001");
            Console.WriteLine("   ✅ Mock DTS Server started");
        }
        catch (Exception ex)
        {
            results["Mock DTS Server"] = (false, ex.Message);
            Console.WriteLine($"   ❌ Failed: {ex.Message}");
            PrintResults(results);
            return;
        }

        // Wait for FRMC to potentially connect to Mock DTS
        Console.WriteLine("   Waiting 5 seconds for FRMC to connect to Mock DTS...");
        await Task.Delay(5000);

        // Test 2: Connect to FRMC WebSocket
        Console.WriteLine("\n2️⃣  Connecting to FRMC WebSocket...");
        using var ws = new ClientWebSocket();
        try
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await ws.ConnectAsync(new Uri(FRMC_WS_URL), cts.Token);
            results["WebSocket Connection"] = (true, "Connected to FRMC");
            Console.WriteLine("   ✅ Connected to FRMC WebSocket");
        }
        catch (Exception ex)
        {
            results["WebSocket Connection"] = (false, ex.Message);
            Console.WriteLine($"   ❌ Failed: {ex.Message}");
            await mockDts.StopAsync();
            PrintResults(results);
            return;
        }

        // Start message listener
        var receivedMessages = new List<(string type, JsonElement payload)>();
        var listenerCts = new CancellationTokenSource();
        var listenerTask = Task.Run(() => ListenForMessages(ws, receivedMessages, listenerCts.Token));

        // Test 3: Authentication
        Console.WriteLine("\n3️⃣  Authenticating...");
        try
        {
            // Try common credentials (testuser is created by reset-user command)
            var credentials = new[] {
                ("testuser", "Test@123"),
                ("admin1", "Admin@123"),
                ("admin", "admin"),
                ("admin", "Admin@123")
            };

            bool loginSuccess = false;
            foreach (var (user, pass) in credentials)
            {
                receivedMessages.Clear();
                await SendMessage(ws, new { MessageType = "login", Payload = new { username = user, password = pass } });
                await Task.Delay(500);

                var resp = receivedMessages.FirstOrDefault(m => m.type == "login_response");
                if (resp.type != null && resp.payload.TryGetProperty("Success", out var s) && s.GetBoolean())
                {
                    Console.WriteLine($"   ✅ Logged in as {user}");
                    loginSuccess = true;
                    break;
                }
            }

            if (!loginSuccess)
            {
                // Final attempt with original credentials for error message
                receivedMessages.Clear();
                await SendMessage(ws, new { MessageType = "login", Payload = new { username = "admin1", password = "Admin@123" } });
            }
            await Task.Delay(500);

            if (loginSuccess)
            {
                results["Authentication"] = (true, "Login successful");
            }
            else
            {
                var loginResponse = receivedMessages.FirstOrDefault(m => m.type == "login_response");
                string errMsg = "Invalid credentials";
                if (loginResponse.type != null && loginResponse.payload.TryGetProperty("Message", out var msg))
                    errMsg = msg.GetString() ?? errMsg;
                results["Authentication"] = (false, $"Login failed: {errMsg}");
                Console.WriteLine($"   ❌ Login failed: {errMsg}");
                Console.WriteLine("   💡 Try checking users.json or use correct credentials");
            }
        }
        catch (Exception ex)
        {
            results["Authentication"] = (false, ex.Message);
            Console.WriteLine($"   ❌ Failed: {ex.Message}");
        }

        // Test 4: Start Live Measurement
        Console.WriteLine("\n4️⃣  Starting Live Measurement...");
        try
        {
            await SendMessage(ws, new { MessageType = "start_measurement", Payload = new { } });
            await Task.Delay(500);
            results["Start Live Measurement"] = (true, "Command sent");
            Console.WriteLine("   ✅ start_measurement command sent");
        }
        catch (Exception ex)
        {
            results["Start Live Measurement"] = (false, ex.Message);
            Console.WriteLine($"   ❌ Failed: {ex.Message}");
        }

        // Test 5: Subscribe to Channel 1
        Console.WriteLine("\n5️⃣  Subscribing to Channel 1...");
        try
        {
            receivedMessages.Clear();
            await SendMessage(ws, new { MessageType = "subscribe_channel", Payload = new { channelId = 1 } });
            await Task.Delay(1000);

            var subResponse = receivedMessages.FirstOrDefault(m => m.type == "subscribe_channel_response");
            if (subResponse.type != null)
            {
                bool success = subResponse.payload.TryGetProperty("Success", out var s) && s.GetBoolean();
                if (success)
                {
                    results["Channel Subscription"] = (true, "Subscribed to channel 1");
                    Console.WriteLine("   ✅ Subscribed to channel 1");
                }
                else
                {
                    string msg = subResponse.payload.TryGetProperty("Message", out var m) ? m.GetString() ?? "Unknown" : "Unknown";
                    results["Channel Subscription"] = (false, msg);
                    Console.WriteLine($"   ❌ Subscription failed: {msg}");
                }
            }
            else
            {
                results["Channel Subscription"] = (false, "No response received");
                Console.WriteLine("   ❌ No subscription response received");
            }
        }
        catch (Exception ex)
        {
            results["Channel Subscription"] = (false, ex.Message);
            Console.WriteLine($"   ❌ Failed: {ex.Message}");
        }

        // Test 6: GetHealthParameters
        Console.WriteLine("\n6️⃣  Testing GetHealthParameters API...");
        try
        {
            receivedMessages.Clear();
            await SendMessage(ws, new { MessageType = "GetHealthParameters", Payload = new { } });
            await Task.Delay(2000);

            var healthResponse = receivedMessages.FirstOrDefault(m => m.type == "GetHealthParameters_response");
            if (healthResponse.type != null)
            {
                bool success = healthResponse.payload.TryGetProperty("Success", out var s) && s.GetBoolean();
                if (success)
                {
                    results["GetHealthParameters"] = (true, "API working");
                    Console.WriteLine("   ✅ GetHealthParameters API working");
                }
                else
                {
                    string error = healthResponse.payload.TryGetProperty("Error", out var e) ? e.GetString() ?? "Unknown" : "Unknown";
                    results["GetHealthParameters"] = (false, error);
                    Console.WriteLine($"   ❌ API failed: {error}");
                }
            }
            else
            {
                results["GetHealthParameters"] = (false, "No response (timeout)");
                Console.WriteLine("   ❌ No response received (possible timeout)");
            }
        }
        catch (Exception ex)
        {
            results["GetHealthParameters"] = (false, ex.Message);
            Console.WriteLine($"   ❌ Failed: {ex.Message}");
        }

        // Test 7: get_active_alarm
        Console.WriteLine("\n7️⃣  Testing get_active_alarm API...");
        try
        {
            receivedMessages.Clear();
            await SendMessage(ws, new { MessageType = "get_active_alarm", Payload = new { } });
            await Task.Delay(1000);

            var alarmResponse = receivedMessages.FirstOrDefault(m => m.type == "active_alarm_response");
            if (alarmResponse.type != null)
            {
                results["get_active_alarm"] = (true, "API working");
                Console.WriteLine("   ✅ get_active_alarm API working");
            }
            else
            {
                results["get_active_alarm"] = (false, "No response");
                Console.WriteLine("   ❌ No response received");
            }
        }
        catch (Exception ex)
        {
            results["get_active_alarm"] = (false, ex.Message);
            Console.WriteLine($"   ❌ Failed: {ex.Message}");
        }

        // Test 8: Wait for broadcast (temperature data)
        Console.WriteLine("\n8️⃣  Waiting for live_temperature_data broadcast (up to 20 seconds)...");
        Console.WriteLine("   [Listening for all message types to diagnose...]");
        try
        {
            receivedMessages.Clear();
            var startTime = DateTime.UtcNow;
            bool receivedBroadcast = false;
            var seenMessageTypes = new HashSet<string>();

            while ((DateTime.UtcNow - startTime).TotalSeconds < 20)
            {
                // Check for broadcast
                var broadcast = receivedMessages.FirstOrDefault(m => m.type == "live_temperature_data");
                if (broadcast.type != null)
                {
                    receivedBroadcast = true;
                    int channelId = 0;
                    int pointCount = 0;
                    if (broadcast.payload.TryGetProperty("channel", out var ch))
                        channelId = ch.GetInt32();
                    if (broadcast.payload.TryGetProperty("data", out var data))
                        pointCount = data.GetArrayLength();

                    results["Live Broadcast"] = (true, $"Channel {channelId}: {pointCount} points");
                    Console.WriteLine($"\n   ✅ Received broadcast: Channel {channelId}, {pointCount:N0} data points");
                    break;
                }

                // Log new message types we haven't seen
                lock (receivedMessages)
                {
                    foreach (var msg in receivedMessages)
                    {
                        if (!seenMessageTypes.Contains(msg.type))
                        {
                            seenMessageTypes.Add(msg.type);
                            Console.WriteLine($"\n   📨 New message type: {msg.type}");
                        }
                    }
                }

                await Task.Delay(500);
                Console.Write(".");
            }

            if (!receivedBroadcast)
            {
                Console.WriteLine($"\n   📊 Received {receivedMessages.Count} messages, types: {string.Join(", ", seenMessageTypes)}");
                results["Live Broadcast"] = (false, $"No broadcast in 20s (received types: {string.Join(", ", seenMessageTypes)})");
                Console.WriteLine("   ❌ No broadcast received - DTS scanning may not be active");
            }
        }
        catch (Exception ex)
        {
            results["Live Broadcast"] = (false, ex.Message);
            Console.WriteLine($"   ❌ Failed: {ex.Message}");
        }

        // Cleanup
        listenerCts.Cancel();
        await mockDts.StopAsync();

        // Print results
        Console.WriteLine();
        PrintResults(results);
    }

    private static async Task ListenForMessages(ClientWebSocket ws, List<(string type, JsonElement payload)> messages, CancellationToken ct)
    {
        var buffer = new byte[2 * 1024 * 1024];

        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close) break;

                string json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    string? msgType = doc.RootElement.GetProperty("MessageType").GetString();
                    JsonElement payload = default;
                    if (doc.RootElement.TryGetProperty("Payload", out payload))
                    {
                        // Clone to preserve beyond document disposal
                        payload = payload.Clone();
                    }
                    if (msgType != null)
                    {
                        lock (messages)
                        {
                            messages.Add((msgType, payload));
                        }
                    }
                }
                catch { }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private static async Task SendMessage(ClientWebSocket ws, object message)
    {
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static void PrintResults(Dictionary<string, (bool success, string message)> results)
    {
        Console.WriteLine("════════════════════════════════════════════════════════════");
        Console.WriteLine("SMOKE TEST RESULTS:");
        Console.WriteLine("════════════════════════════════════════════════════════════");

        int passed = 0, failed = 0;
        foreach (var (test, result) in results)
        {
            string status = result.success ? "✅ PASS" : "❌ FAIL";
            Console.WriteLine($"  {status} - {test}: {result.message}");
            if (result.success) passed++; else failed++;
        }

        Console.WriteLine("════════════════════════════════════════════════════════════");
        Console.WriteLine($"Total: {passed} passed, {failed} failed");

        if (failed == 0)
            Console.WriteLine("\n✅ All smoke tests passed! Ready for heavy load testing.");
        else
            Console.WriteLine("\n⚠️  Some tests failed. Fix issues before running heavy load test.");
    }
}
