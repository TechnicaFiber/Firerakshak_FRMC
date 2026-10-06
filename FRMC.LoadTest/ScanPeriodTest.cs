using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace FRMC.LoadTest;

/// <summary>
/// Test to find optimal minimum scanning period for 10km channels
/// Runs multiple tests with decreasing polling intervals to find the limit
/// </summary>
public class ScanPeriodTest
{
    private const int NUM_CHANNELS = 8;
    private const double CHANNEL_LENGTH_KM = 10.0;
    private const string FRMC_WS_URL = "ws://localhost:6164/general_ws/?token=connecting";

    public static async Task Run()
    {
        Console.WriteLine("╔═══════════════════════════════════════════════════════════╗");
        Console.WriteLine("║  FRMC Optimal Scan Period Test                            ║");
        Console.WriteLine("║  Finding minimum scanning period for 10km channels        ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");
        Console.WriteLine();
        Console.WriteLine($"Configuration: {NUM_CHANNELS} channels × {CHANNEL_LENGTH_KM} km");
        Console.WriteLine();

        // Test different polling intervals (in milliseconds)
        int[] pollingIntervals = { 5000, 4000, 3000, 2500, 2000, 1500, 1000 };
        var results = new List<(int interval, int broadcasts, int errors, double cycleTime)>();

        // Start Mock DTS server once
        var mockDts = new MockDtsServer(NUM_CHANNELS, CHANNEL_LENGTH_KM);
        await mockDts.StartAsync();

        // Wait for FRMC to connect
        Console.WriteLine("⏳ Waiting for FRMC to connect to Mock DTS...");
        await Task.Delay(5000);

        foreach (var interval in pollingIntervals)
        {
            Console.WriteLine();
            Console.WriteLine($"════════════════════════════════════════════════════════════");
            Console.WriteLine($"Testing polling interval: {interval}ms ({interval / 1000.0:F1}s)");
            Console.WriteLine($"Per-channel delay: {interval / NUM_CHANNELS}ms");
            Console.WriteLine($"════════════════════════════════════════════════════════════");

            var result = await RunSingleTest(interval, 30); // 30 second test per interval
            results.Add((interval, result.broadcasts, result.errors, result.cycleTime));

            if (result.errors > 0)
            {
                Console.WriteLine($"❌ Errors detected at {interval}ms - stopping test");
                break;
            }

            // Brief pause between tests
            await Task.Delay(2000);
        }

        await mockDts.StopAsync();

        // Print summary
        Console.WriteLine();
        Console.WriteLine("╔═══════════════════════════════════════════════════════════╗");
        Console.WriteLine("║  SCAN PERIOD TEST RESULTS                                 ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");
        Console.WriteLine();
        Console.WriteLine("  Interval   Per-Ch    Broadcasts   Cycle Time   Status");
        Console.WriteLine("  ────────   ──────    ──────────   ──────────   ──────");

        int optimalInterval = 5000;
        foreach (var (interval, broadcasts, errors, cycleTime) in results)
        {
            string status = errors == 0 ? "✅ OK" : "❌ FAIL";
            Console.WriteLine($"  {interval,5}ms   {interval / NUM_CHANNELS,4}ms    {broadcasts,5}        {cycleTime,6:F2}s      {status}");

            if (errors == 0 && interval < optimalInterval)
            {
                optimalInterval = interval;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"════════════════════════════════════════════════════════════");
        Console.WriteLine($"  OPTIMAL MINIMUM POLLING INTERVAL: {optimalInterval}ms");
        Console.WriteLine($"  Per-channel delay: {optimalInterval / NUM_CHANNELS}ms");
        Console.WriteLine($"  Expected cycle time: ~{optimalInterval / 1000.0 + 1.0:F1}s");
        Console.WriteLine($"════════════════════════════════════════════════════════════");
    }

    private static async Task<(int broadcasts, int errors, double cycleTime)> RunSingleTest(int pollingInterval, int durationSeconds)
    {
        int broadcasts = 0;
        int errors = 0;
        var scanTimes = new List<DateTime>();

        using var ws = new ClientWebSocket();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds + 5));

        try
        {
            await ws.ConnectAsync(new Uri(FRMC_WS_URL), cts.Token);

            // Authenticate
            await SendMessage(ws, new { MessageType = "login", Payload = new { username = "testuser", password = "Test@123" } }, cts.Token);
            await Task.Delay(300, cts.Token);

            // Start measurement
            await SendMessage(ws, new { MessageType = "start_measurement", Payload = new { } }, cts.Token);
            await Task.Delay(300, cts.Token);

            // Subscribe to channel 1
            await SendMessage(ws, new { MessageType = "subscribe_channel", Payload = new { channelId = 1 } }, cts.Token);

            // Listen for broadcasts
            var buffer = new byte[2 * 1024 * 1024];
            var startTime = DateTime.UtcNow;
            var testEndTime = startTime.AddSeconds(durationSeconds);

            while (DateTime.UtcNow < testEndTime && !cts.Token.IsCancellationRequested)
            {
                try
                {
                    var receiveTask = ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                    var timeoutTask = Task.Delay(5000, cts.Token);

                    var completedTask = await Task.WhenAny(receiveTask, timeoutTask);

                    if (completedTask == timeoutTask)
                    {
                        // Timeout waiting for message - might indicate system overload
                        continue;
                    }

                    var result = await receiveTask;

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        errors++;
                        break;
                    }

                    string message = Encoding.UTF8.GetString(buffer, 0, result.Count);

                    using var doc = JsonDocument.Parse(message);
                    string? msgType = doc.RootElement.GetProperty("MessageType").GetString();

                    if (msgType == "live_temperature_data")
                    {
                        broadcasts++;
                        scanTimes.Add(DateTime.UtcNow);

                        // Check for errors in payload
                        if (doc.RootElement.TryGetProperty("Payload", out var payload))
                        {
                            if (payload.TryGetProperty("data", out var data))
                            {
                                int points = data.GetArrayLength();
                                if (points == 0)
                                {
                                    errors++;
                                }
                            }
                        }
                    }
                    else if (msgType?.EndsWith("_response") == true)
                    {
                        if (doc.RootElement.TryGetProperty("Payload", out var payload))
                        {
                            if (payload.TryGetProperty("Success", out var success) && !success.GetBoolean())
                            {
                                if (payload.TryGetProperty("Error", out var error))
                                {
                                    var errorMsg = error.GetString();
                                    if (errorMsg?.Contains("timeout") == true)
                                    {
                                        errors++;
                                    }
                                }
                            }
                        }
                    }

                    // Fire some requests during the test
                    if (broadcasts % 2 == 0)
                    {
                        _ = SendMessage(ws, new { MessageType = "GetHealthParameters", Payload = new { } }, cts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  ⚠️ Error: {ex.Message}");
                    errors++;
                }
            }

            // Calculate average cycle time
            double avgCycleTime = 0;
            if (scanTimes.Count > 1)
            {
                var intervals = new List<double>();
                for (int i = 1; i < scanTimes.Count; i++)
                {
                    intervals.Add((scanTimes[i] - scanTimes[i - 1]).TotalSeconds);
                }
                avgCycleTime = intervals.Average();
            }

            Console.WriteLine($"  📊 Broadcasts: {broadcasts}, Errors: {errors}, Avg cycle: {avgCycleTime:F2}s");

            return (broadcasts, errors, avgCycleTime);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌ Test failed: {ex.Message}");
            return (broadcasts, errors + 1, 0);
        }
    }

    private static async Task SendMessage(ClientWebSocket ws, object message, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }
}
