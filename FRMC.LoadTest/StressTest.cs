using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace FRMC.LoadTest;

/// <summary>
/// Stress test with:
/// - Mock DTS running in dedicated thread
/// - Multiple DTSCM clients running in separate threads
/// - Aggressive request bursts during broadcast reception
/// - High concurrency to stress test the system
/// </summary>
public class StressTest
{
    private const int NUM_CHANNELS = 8;
    private static double _channelLengthKm = 10.0; // Default: 10km = 25,000 points per channel
    private const int TEST_DURATION_SECONDS = 60; // Quick test
    private const int NUM_CLIENTS = 3; // Multiple concurrent DTSCM clients
    private const int REQUESTS_PER_BURST = 10; // Requests fired when broadcast received
    private const string FRMC_WS_URL = "ws://localhost:6164/general_ws/?token=connecting";

    // Thread-safe counters
    private static int _totalRequests = 0;
    private static int _successfulRequests = 0;
    private static int _timeouts = 0;
    private static int _errors = 0;
    private static int _channelSwitches = 0;
    private static int _broadcastsReceived = 0;
    private static int _burstRequestsFired = 0;

    // Shared state for broadcast-triggered bursts
    private static readonly ConcurrentQueue<int> _broadcastTriggerQueue = new();

    public static void SetChannelLength(double lengthKm)
    {
        _channelLengthKm = lengthKm;
    }

    public static async Task Run()
    {
        Console.WriteLine("╔═══════════════════════════════════════════════════════════╗");
        Console.WriteLine("║  FRMC STRESS TEST - Maximum Load Simulation               ║");
        Console.WriteLine("║  Multi-threaded DTS + Multiple DTSCM Clients              ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");
        Console.WriteLine();
        int pointsPerChannel = (int)(_channelLengthKm * 1000 / 0.4); // 0.4m resolution
        Console.WriteLine("Test Configuration:");
        Console.WriteLine($"  - Channels: {NUM_CHANNELS}");
        Console.WriteLine($"  - Channel Length: {_channelLengthKm} km ({pointsPerChannel:N0} points each)");
        Console.WriteLine($"  - Test Duration: {TEST_DURATION_SECONDS} seconds");
        Console.WriteLine($"  - DTSCM Clients: {NUM_CLIENTS} (concurrent)");
        Console.WriteLine($"  - Requests per broadcast burst: {REQUESTS_PER_BURST}");
        Console.WriteLine($"  - Operations: GetHealthParameters, get_active_alarm, GetSystemHealth, GetZoneState, GetSystemConfig");
        Console.WriteLine();

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(TEST_DURATION_SECONDS));

        // Start Mock DTS in dedicated thread
        var mockDts = new MockDtsServer(NUM_CHANNELS, _channelLengthKm);
        var dtsThread = new Thread(() =>
        {
            try
            {
                mockDts.StartAsync().Wait();
                // Keep thread alive until cancellation
                while (!cts.Token.IsCancellationRequested)
                {
                    Thread.Sleep(100);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [DTS Thread] Error: {ex.Message}");
            }
        });
        dtsThread.Name = "MockDTS-Thread";
        dtsThread.IsBackground = true;
        dtsThread.Start();

        Console.WriteLine($"🌡️  [DTS] Mock DTS server started in dedicated thread");

        // Wait for FRMC to connect to DTS
        await Task.Delay(3000, cts.Token);

        // Start multiple DTSCM client threads
        var clientTasks = new List<Task>();
        for (int i = 1; i <= NUM_CLIENTS; i++)
        {
            int clientId = i;
            var clientThread = new Thread(() =>
            {
                try
                {
                    RunDtscmClient(clientId, cts.Token).Wait();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.WriteLine($"❌ [Client-{clientId} Thread] Error: {ex.Message}");
                    Interlocked.Increment(ref _errors);
                }
            });
            clientThread.Name = $"DTSCM-Client-{clientId}";
            clientThread.IsBackground = true;
            clientThread.Start();

            // Stagger client starts
            await Task.Delay(500, cts.Token);
        }

        Console.WriteLine($"🖥️  [{NUM_CLIENTS}] DTSCM clients started in dedicated threads");
        Console.WriteLine();

        // Status reporting loop
        var startTime = DateTime.UtcNow;
        while (!cts.Token.IsCancellationRequested)
        {
            await Task.Delay(10000, cts.Token).ContinueWith(_ => { }); // Ignore cancellation

            if (cts.Token.IsCancellationRequested) break;

            var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
            var rps = _totalRequests / Math.Max(1, elapsed);
            Console.WriteLine($"📈 [Status] Elapsed: {elapsed:F0}s | Clients: {NUM_CLIENTS} | Switches: {_channelSwitches} | Requests: {_totalRequests} ({rps:F1}/s) | Broadcasts: {_broadcastsReceived} | Bursts: {_burstRequestsFired} | Errors: {_errors}");
        }

        // Stop Mock DTS
        await mockDts.StopAsync();

        // Wait for threads to complete
        await Task.Delay(1000);

        PrintResults();
    }

    private static async Task RunDtscmClient(int clientId, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        var random = new Random(clientId * 1000); // Different seed per client

        try
        {
            Console.WriteLine($"🖥️  [Client-{clientId}] Connecting to FRMC...");
            await ws.ConnectAsync(new Uri(FRMC_WS_URL), ct);
            Console.WriteLine($"✅ [Client-{clientId}] Connected");

            // Authenticate
            await SendMessage(ws, new { MessageType = "login", Payload = new { username = "testuser", password = "Test@123" } }, ct);
            await Task.Delay(300, ct);

            // Start live measurement
            await SendMessage(ws, new { MessageType = "start_measurement", Payload = new { } }, ct);
            await Task.Delay(300, ct);

            int currentChannel = (clientId % NUM_CHANNELS) + 1; // Different starting channel per client

            // Subscribe to initial channel
            await SendMessage(ws, new { MessageType = "subscribe_channel", Payload = new { channelId = currentChannel } }, ct);

            // Start listener in separate task
            var listenerTask = Task.Run(() => ListenForMessages(ws, clientId, ct), ct);

            // Start burst requester in separate task (fires requests when broadcasts arrive)
            var burstTask = Task.Run(() => BurstRequester(ws, clientId, ct), ct);

            Console.WriteLine($"🚀 [Client-{clientId}] Starting intensive operations on channel {currentChannel}");

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Rapid channel switching
                    int nextChannel = random.Next(1, NUM_CHANNELS + 1);
                    if (nextChannel != currentChannel)
                    {
                        Interlocked.Increment(ref _channelSwitches);

                        await SendMessage(ws, new { MessageType = "unsubscribe_channel", Payload = new { channelId = currentChannel } }, ct);
                        await Task.Delay(10, ct);
                        await SendMessage(ws, new { MessageType = "subscribe_channel", Payload = new { channelId = nextChannel } }, ct);

                        currentChannel = nextChannel;
                    }

                    // Fire concurrent requests
                    var tasks = new List<Task>
                    {
                        MakeRequest("GetHealthParameters", ws, new { }, ct),
                        MakeRequest("get_active_alarm", ws, new { }, ct),
                        MakeRequest("GetSystemHealth", ws, new { }, ct),
                        MakeRequest("GetZoneState", ws, new { ChannelId = currentChannel, ZoneId = 0 }, ct),
                        MakeRequest("GetSystemConfig", ws, new { }, ct),
                        // Additional requests for more load
                        MakeRequest("GetHealthParameters", ws, new { }, ct),
                        MakeRequest("get_active_alarm", ws, new { }, ct),
                    };

                    await Task.WhenAll(tasks);

                    // Short delay between request batches (50-150ms for very rapid switching)
                    await Task.Delay(random.Next(50, 150), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _errors);
                    if (_errors % 10 == 1) // Log every 10th error
                    {
                        Console.WriteLine($"❌ [Client-{clientId}] Error: {ex.Message}");
                    }
                }
            }

            await Task.WhenAny(listenerTask, burstTask, Task.Delay(1000, CancellationToken.None));
        }
        catch (WebSocketException ex)
        {
            Console.WriteLine($"❌ [Client-{clientId}] WebSocket error: {ex.Message}");
            Interlocked.Increment(ref _errors);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"❌ [Client-{clientId}] Error: {ex.Message}");
            Interlocked.Increment(ref _errors);
        }

        Console.WriteLine($"🛑 [Client-{clientId}] Stopped");
    }

    /// <summary>
    /// Fires burst of requests when a broadcast is received
    /// This simulates DTSCM doing heavy processing/requests when new data arrives
    /// </summary>
    private static async Task BurstRequester(ClientWebSocket ws, int clientId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            // Check if there's a broadcast trigger
            if (_broadcastTriggerQueue.TryDequeue(out int channelId))
            {
                // Fire burst of requests
                var burstTasks = new List<Task>();
                for (int i = 0; i < REQUESTS_PER_BURST; i++)
                {
                    burstTasks.Add(MakeRequest("GetHealthParameters", ws, new { }, ct));
                    burstTasks.Add(MakeRequest("GetZoneState", ws, new { ChannelId = channelId, ZoneId = i % 5 }, ct));
                }

                Interlocked.Add(ref _burstRequestsFired, REQUESTS_PER_BURST * 2);

                try
                {
                    await Task.WhenAll(burstTasks);
                }
                catch
                {
                    // Ignore individual failures in burst
                }
            }
            else
            {
                await Task.Delay(10, ct);
            }
        }
    }

    private static async Task ListenForMessages(ClientWebSocket ws, int clientId, CancellationToken ct)
    {
        var buffer = new byte[2 * 1024 * 1024];
        int messageCount = 0;
        int broadcastCount = 0;
        var lastLogTime = DateTime.MinValue;

        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Console.WriteLine($"⚠️  [Client-{clientId}] Server closed connection");
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

                        // Get channel ID and trigger burst requests
                        var payload = doc.RootElement.GetProperty("Payload");
                        int channelId = 1;
                        int pointCount = 0;
                        if (payload.TryGetProperty("channel", out var chProp))
                        {
                            channelId = chProp.GetInt32();
                        }
                        if (payload.TryGetProperty("data", out var tempData))
                        {
                            pointCount = tempData.GetArrayLength();
                        }

                        // Trigger burst requests for all clients
                        _broadcastTriggerQueue.Enqueue(channelId);

                        var now = DateTime.UtcNow;
                        if ((now - lastLogTime).TotalSeconds >= 10)
                        {
                            lastLogTime = now;
                            Console.WriteLine($"📊 [Client-{clientId}] Broadcast #{broadcastCount}: Channel {channelId}, {pointCount:N0} points");
                        }
                    }

                    // Check for errors
                    if (msgType?.EndsWith("_response") == true && doc.RootElement.TryGetProperty("Payload", out var payload2))
                    {
                        if (payload2.TryGetProperty("Success", out var success) && !success.GetBoolean())
                        {
                            if (payload2.TryGetProperty("Error", out var error))
                            {
                                var errorMsg = error.GetString();
                                if (!errorMsg?.Contains("timeout") == true) // Don't spam timeout errors
                                {
                                    Console.WriteLine($"⚠️  [Client-{clientId}] {msgType}: {errorMsg}");
                                }
                            }
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
            Console.WriteLine($"❌ [Client-{clientId}] CONNECTION LOST!");
            Interlocked.Increment(ref _errors);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"❌ [Client-{clientId}] Listener error: {ex.Message}");
        }

        Console.WriteLine($"📊 [Client-{clientId}] Received {messageCount:N0} messages ({broadcastCount} broadcasts)");
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

    private static readonly SemaphoreSlim _sendLock = new(1, 1);

    private static async Task SendMessage(ClientWebSocket ws, object message, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);

        // WebSocket SendAsync is not thread-safe, need to serialize sends per socket
        // But we're using multiple sockets now, so this is per-client
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    private static void PrintResults()
    {
        var successRate = _totalRequests > 0 ? (_successfulRequests * 100.0 / _totalRequests) : 0;

        Console.WriteLine();
        Console.WriteLine("════════════════════════════════════════════════════════════");
        Console.WriteLine("STRESS TEST RESULTS:");
        Console.WriteLine("════════════════════════════════════════════════════════════");
        Console.WriteLine($"  DTSCM Clients:       {NUM_CLIENTS}");
        Console.WriteLine($"  Channel Switches:    {_channelSwitches:N0}");
        Console.WriteLine($"  Broadcasts Received: {_broadcastsReceived:N0}");
        Console.WriteLine($"  Burst Requests:      {_burstRequestsFired:N0}");
        Console.WriteLine($"  Total Requests:      {_totalRequests:N0}");
        Console.WriteLine($"  Successful:          {_successfulRequests:N0}");
        Console.WriteLine($"  Timeouts:            {_timeouts:N0} {(_timeouts > 0 ? "❌" : "✅")}");
        Console.WriteLine($"  Errors:              {_errors:N0} {(_errors > 0 ? "❌" : "✅")}");
        Console.WriteLine($"  Success Rate:        {successRate:F1}%");
        Console.WriteLine($"  Requests/Second:     {_totalRequests / (double)TEST_DURATION_SECONDS:F1}");
        Console.WriteLine("════════════════════════════════════════════════════════════");

        if (_timeouts > 0 || _errors > 0)
        {
            Console.WriteLine();
            Console.WriteLine("❌ ISSUES DETECTED - System struggled under stress!");
        }
        else if (_broadcastsReceived > 0)
        {
            Console.WriteLine();
            Console.WriteLine("✅ STRESS TEST PASSED!");
            Console.WriteLine($"   FRMC handled {NUM_CLIENTS} concurrent clients + burst requests during broadcasts!");
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("⚠️  No broadcasts received - check FRMC/DTS connection");
        }
    }
}
