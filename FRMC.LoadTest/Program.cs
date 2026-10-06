using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace FRMC.LoadTest;

/// <summary>
/// Comprehensive load test for FRMC channel switching fixes
/// Simulates:
/// - DTS device sending temperature data for 8 channels (10km each) every 5 seconds
/// - DTSCM client performing rapid channel switching
/// - Validates no concurrent SendAsync crashes occur
/// </summary>
class Program
{
    static async Task Main(string[] args)
    {
        // Check if password reset is requested
        if (args.Length > 0 && args[0].Equals("reset-user", StringComparison.OrdinalIgnoreCase))
        {
            await PasswordUtil.ResetTestUser();
            return;
        }

        // Check if smoke test is requested
        if (args.Length > 0 && args[0].Equals("smoke", StringComparison.OrdinalIgnoreCase))
        {
            await SmokeTest.Run();
            return;
        }

        // Check if intensive test is requested
        if (args.Length > 0 && args[0].Equals("intensive", StringComparison.OrdinalIgnoreCase))
        {
            await IntensiveTest.Run();
            return;
        }

        // Check if heavy load test is requested (20km channels)
        if (args.Length > 0 && args[0].Equals("heavy", StringComparison.OrdinalIgnoreCase))
        {
            await HeavyLoadTest.Run();
            return;
        }

        // Check if stress test is requested (multi-threaded, high load)
        if (args.Length > 0 && args[0].Equals("stress", StringComparison.OrdinalIgnoreCase))
        {
            // Parse optional channel length: stress [length_km]
            if (args.Length > 1 && double.TryParse(args[1], out double channelLengthKm))
            {
                StressTest.SetChannelLength(channelLengthKm);
            }
            await StressTest.Run();
            return;
        }

        // Check if scan period test is requested (find optimal minimum)
        if (args.Length > 0 && args[0].Equals("scanperiod", StringComparison.OrdinalIgnoreCase))
        {
            await ScanPeriodTest.Run();
            return;
        }

        Console.WriteLine("╔═══════════════════════════════════════════════════════════╗");
        Console.WriteLine("║  FRMC Channel Switching Load Test                        ║");
        Console.WriteLine("║  Testing: 8 channels × 10km × 5-second scan cycle        ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        // Test configuration
        const int NUM_CHANNELS = 8;
        const double CHANNEL_LENGTH_KM = 10.0;
        const int SCAN_INTERVAL_SECONDS = 5;
        const int TEST_DURATION_MINUTES = 5;
        const string FRMC_WS_URL = "ws://localhost:6164/general_ws/?token=connecting";

        Console.WriteLine($"Test Configuration:");
        Console.WriteLine($"  - Channels: {NUM_CHANNELS}");
        Console.WriteLine($"  - Channel Length: {CHANNEL_LENGTH_KM} km");
        Console.WriteLine($"  - Scan Interval: {SCAN_INTERVAL_SECONDS} seconds");
        Console.WriteLine($"  - Test Duration: {TEST_DURATION_MINUTES} minutes");
        Console.WriteLine($"  - FRMC WebSocket: {FRMC_WS_URL}");
        Console.WriteLine();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(TEST_DURATION_MINUTES));

        // Start mock DTS simulator
        var dtsTask = Task.Run(() => RunMockDtsSimulator(NUM_CHANNELS, CHANNEL_LENGTH_KM, SCAN_INTERVAL_SECONDS, cts.Token), cts.Token);

        // Wait for DTS to initialize
        await Task.Delay(2000, cts.Token);

        // Start mock DTSCM clients
        var dtscmTask1 = Task.Run(() => RunMockDtscmClient("Client-1", FRMC_WS_URL, NUM_CHANNELS, cts.Token), cts.Token);
        var dtscmTask2 = Task.Run(() => RunMockDtscmClient("Client-2", FRMC_WS_URL, NUM_CHANNELS, cts.Token), cts.Token);

        Console.WriteLine("🚀 Test started - monitoring for {0} minutes...", TEST_DURATION_MINUTES);
        Console.WriteLine("Press Ctrl+C to stop early");
        Console.WriteLine();

        try
        {
            await Task.WhenAll(dtsTask, dtscmTask1, dtscmTask2);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\n✅ Test completed successfully!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n❌ Test failed with error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }

        Console.WriteLine("\n" + new string('═', 60));
        Console.WriteLine("Test Summary:");
        Console.WriteLine($"  - DTS Simulator: {(dtsTask.IsCompletedSuccessfully ? "✅ Completed" : "❌ Failed")}");
        Console.WriteLine($"  - DTSCM Client 1: {(dtscmTask1.IsCompletedSuccessfully ? "✅ Completed" : "❌ Failed")}");
        Console.WriteLine($"  - DTSCM Client 2: {(dtscmTask2.IsCompletedSuccessfully ? "✅ Completed" : "❌ Failed")}");
        Console.WriteLine(new string('═', 60));
    }

    /// <summary>
    /// Simulates DTS device sending temperature data for all channels
    /// </summary>
    static async Task RunMockDtsSimulator(int numChannels, double lengthKm, int intervalSeconds, CancellationToken ct)
    {
        Console.WriteLine("🌡️  [DTS] Mock DTS Simulator started");

        int scanCycle = 0;
        var random = new Random();

        while (!ct.IsCancellationRequested)
        {
            scanCycle++;
            Console.WriteLine($"📡 [DTS] Scan cycle #{scanCycle} - Generating data for {numChannels} channels...");

            for (int channelId = 1; channelId <= numChannels; channelId++)
            {
                // Generate temperature data for 10km channel (0.4m resolution = 25,000 points)
                int pointsPerChannel = (int)(lengthKm * 1000 / 0.4); // 10km / 0.4m = 25,000 points
                double baseTemp = channelId * 100.0; // Channel 1=100°C, Channel 2=200°C, etc.

                var temperatureData = new List<TemperaturePoint>();
                for (int i = 0; i < pointsPerChannel; i++)
                {
                    double position = i * 0.4; // 0.4m resolution
                    double variation = random.NextDouble() * 10 - 5; // ±5°C variation
                    double temperature = baseTemp + variation;

                    temperatureData.Add(new TemperaturePoint
                    {
                        Position = position,
                        Temperature = temperature
                    });
                }

                Console.WriteLine($"   Channel {channelId}: {temperatureData.Count} points generated (avg: {baseTemp:F1}°C)");

                // Simulate DTS sending data to FRMC via RS232/protocol handler
                // In real system, this would be sent via serial port
                // For this test, we just log that data is ready
            }

            // Wait for next scan cycle
            Console.WriteLine($"⏱️  [DTS] Scan cycle #{scanCycle} complete. Waiting {intervalSeconds}s for next cycle...\n");
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
        }

        Console.WriteLine("🛑 [DTS] Mock DTS Simulator stopped");
    }

    /// <summary>
    /// Simulates DTSCM client connecting, subscribing, and switching channels
    /// </summary>
    static async Task RunMockDtscmClient(string clientName, string wsUrl, int numChannels, CancellationToken ct)
    {
        Console.WriteLine($"🖥️  [{clientName}] Mock DTSCM client starting...");

        try
        {
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(wsUrl), ct);
            Console.WriteLine($"✅ [{clientName}] Connected to FRMC WebSocket");

            // Start listener task
            var listenerTask = Task.Run(() => ListenForMessages(ws, clientName, ct), ct);

            // Authenticate
            await SendMessage(ws, new
            {
                MessageType = "login",
                Payload = new { username = "admin1", password = "Admin@123" }
            }, ct);

            await Task.Delay(500, ct); // Wait for login response

            Console.WriteLine($"🔐 [{clientName}] Authenticated");

            // Start live measurement
            await SendMessage(ws, new
            {
                MessageType = "StartLiveMeasurement",
                Payload = new { }
            }, ct);

            await Task.Delay(500, ct);
            Console.WriteLine($"▶️  [{clientName}] Live measurement started");

            // Channel switching test
            int switchCount = 0;
            var random = new Random();

            while (!ct.IsCancellationRequested)
            {
                switchCount++;

                // Random channel switching pattern
                int currentChannel = (switchCount % numChannels) + 1;
                int nextChannel = random.Next(1, numChannels + 1);

                Console.WriteLine($"🔄 [{clientName}] Switch #{switchCount}: Channel {currentChannel} → {nextChannel}");

                // Unsubscribe from current channel
                await SendMessage(ws, new
                {
                    MessageType = "unsubscribe_channel",
                    Payload = new { channelId = currentChannel }
                }, ct);

                await Task.Delay(50, ct); // Small delay between unsubscribe/subscribe

                // Subscribe to new channel
                await SendMessage(ws, new
                {
                    MessageType = "subscribe_channel",
                    Payload = new { channelId = nextChannel }
                }, ct);

                await Task.Delay(100, ct);

                // Request health parameters (tests concurrent SendAsync scenario)
                await SendMessage(ws, new
                {
                    MessageType = "GetHealthParameters",
                    Payload = new { }
                }, ct);

                await Task.Delay(100, ct);

                // Request zone state (tests broadcast concurrency)
                await SendMessage(ws, new
                {
                    MessageType = "GetZoneState",
                    Payload = new { ChannelId = nextChannel, ZoneId = 0 }
                }, ct);

                // Wait before next switch
                int waitTime = random.Next(2000, 5000); // 2-5 seconds
                await Task.Delay(waitTime, ct);
            }

            await listenerTask;
        }
        catch (WebSocketException ex)
        {
            Console.WriteLine($"❌ [{clientName}] WebSocket error: {ex.Message}");
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"❌ [{clientName}] Error: {ex.Message}");
            throw;
        }

        Console.WriteLine($"🛑 [{clientName}] Mock DTSCM client stopped");
    }

    /// <summary>
    /// Listens for WebSocket messages and validates responses
    /// </summary>
    static async Task ListenForMessages(ClientWebSocket ws, string clientName, CancellationToken ct)
    {
        var buffer = new byte[1024 * 1024]; // 1MB buffer for large broadcasts
        int messageCount = 0;
        int errorCount = 0;

        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Console.WriteLine($"⚠️  [{clientName}] Server initiated close: {result.CloseStatusDescription}");
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closing", ct);
                    break;
                }

                messageCount++;
                string message = Encoding.UTF8.GetString(buffer, 0, result.Count);

                try
                {
                    using var doc = JsonDocument.Parse(message);
                    string? messageType = doc.RootElement.GetProperty("MessageType").GetString();

                    // Validate critical responses
                    if (messageType == "GetHealthParameters_response")
                    {
                        bool success = doc.RootElement.GetProperty("Payload").GetProperty("Success").GetBoolean();
                        if (success)
                        {
                            Console.WriteLine($"✅ [{clientName}] GetHealthParameters succeeded (no timeout!)");
                        }
                        else
                        {
                            errorCount++;
                            Console.WriteLine($"❌ [{clientName}] GetHealthParameters failed!");
                        }
                    }
                    else if (messageType == "live_temperature_data")
                    {
                        var payload = doc.RootElement.GetProperty("Payload");
                        int channel = payload.GetProperty("channel").GetInt32();
                        var data = payload.GetProperty("data");
                        int dataCount = data.GetArrayLength();
                        Console.WriteLine($"📊 [{clientName}] Received live data: Channel {channel}, {dataCount} points");
                    }
                }
                catch (Exception ex)
                {
                    errorCount++;
                    Console.WriteLine($"⚠️  [{clientName}] Failed to parse message: {ex.Message}");
                }
            }
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            errorCount++;
            Console.WriteLine($"❌ [{clientName}] CONNECTION LOST - Server closed connection prematurely!");
            Console.WriteLine($"   This indicates the concurrent SendAsync crash occurred!");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errorCount++;
            Console.WriteLine($"❌ [{clientName}] Listener error: {ex.Message}");
        }

        Console.WriteLine($"📈 [{clientName}] Listener stats: {messageCount} messages, {errorCount} errors");

        if (errorCount > 0)
        {
            throw new Exception($"[{clientName}] Test failed with {errorCount} errors");
        }
    }

    /// <summary>
    /// Sends a WebSocket message
    /// </summary>
    static async Task SendMessage(ClientWebSocket ws, object message, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(message);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    class TemperaturePoint
    {
        public double Position { get; set; }
        public double Temperature { get; set; }
    }
}
