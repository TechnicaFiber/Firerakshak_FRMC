using System.Net;
using System.Net.Sockets;

namespace FRMC.LoadTest;

/// <summary>
/// Mock DTS TCP server that responds to FRMC commands with simulated temperature data
/// Implements the DTS protocol to trigger real broadcasts in FRMC
/// </summary>
public class MockDtsServer
{
    private const int DTS_PORT = 3001;
    private readonly int _numChannels;
    private readonly double _channelLengthKm;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _serverTask;

    public MockDtsServer(int numChannels, double channelLengthKm)
    {
        _numChannels = numChannels;
        _channelLengthKm = channelLengthKm;
    }

    public async Task StartAsync()
    {
        _listener = new TcpListener(IPAddress.Any, DTS_PORT);
        _listener.Start();
        _cts = new CancellationTokenSource();

        Console.WriteLine($"🌡️  [Mock DTS] Server started on port {DTS_PORT}");
        Console.WriteLine($"   Simulating {_numChannels} channels × {_channelLengthKm}km");

        _serverTask = Task.Run(() => AcceptClientsAsync(_cts.Token));
        await Task.Delay(100); // Let server start
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();
        if (_serverTask != null)
        {
            await _serverTask;
        }
        Console.WriteLine("🛑 [Mock DTS] Server stopped");
    }

    private async Task AcceptClientsAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                Console.WriteLine($"✅ [Mock DTS] FRMC connected from {client.Client.RemoteEndPoint}");

                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ [Mock DTS] Accept error: {ex.Message}");
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var buffer = new byte[1024];
                int commandCount = 0;

                while (!ct.IsCancellationRequested && client.Connected)
                {
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
                    if (bytesRead == 0) break;

                    // Parse command
                    if (bytesRead >= 10 && buffer[0] == 0xCC && buffer[1] == 0x55)
                    {
                        byte cmd = buffer[7];
                        byte channel = buffer[8];

                        commandCount++;

                        switch (cmd)
                        {
                            case 0x07: // Read Temperature
                                var response = BuildTemperatureResponse(channel);
                                await stream.WriteAsync(response, 0, response.Length, ct);
                                Console.WriteLine($"📊 [Mock DTS] Temp request #{commandCount}: Channel {channel}, sent {response.Length:N0} bytes ({response.Length / 1024}KB)");
                                break;

                            case 0x0D: // Read Fiber Break Alarm
                                var fiberResponse = BuildFiberBreakResponse();
                                await stream.WriteAsync(fiberResponse, 0, fiberResponse.Length, ct);
                                Console.WriteLine($"🔗 [Mock DTS] Fiber break request #{commandCount}");
                                break;

                            case 0x03: // Internal commands (health parameters)
                                var healthResponse = BuildHealthResponse(buffer);
                                await stream.WriteAsync(healthResponse, 0, healthResponse.Length, ct);
                                if (commandCount <= 5 || commandCount % 20 == 0)
                                    Console.WriteLine($"💓 [Mock DTS] Health/internal cmd #{commandCount}");
                                break;

                            default:
                                Console.WriteLine($"⚠️  [Mock DTS] Unknown command: 0x{cmd:X2} (bytes: {bytesRead})");
                                break;
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"⚠️  [Mock DTS] Client handler error: {ex.Message}");
        }
    }

    private byte[] BuildTemperatureResponse(byte channel)
    {
        // Calculate number of points: channelLength(km) * 1000 / 0.4m resolution
        int numPoints = (int)(_channelLengthKm * 1000 / 0.4);

        // Response format: FLAG2 (2) + LEN (4) + ADR (1) + CMD (1) + CH (1) + ID (4) + T_DATA (N*2) + SUM (1)
        int dataBytes = numPoints * 2; // 2 bytes per temperature point
        int totalLength = 14 + dataBytes;
        var response = new byte[totalLength];

        // FLAG2
        response[0] = 0xCC;
        response[1] = 0xAA;

        // LEN (data length = ADR + CMD + CH + ID + T_DATA + SUM)
        int len = 1 + 1 + 1 + 4 + dataBytes + 1;
        response[2] = (byte)((len >> 24) & 0xFF);
        response[3] = (byte)((len >> 16) & 0xFF);
        response[4] = (byte)((len >> 8) & 0xFF);
        response[5] = (byte)(len & 0xFF);

        // ADR
        response[6] = 0xFF;

        // CMD
        response[7] = 0x07;

        // CH
        response[8] = channel;

        // ID (4 bytes) - scan ID
        uint scanId = (uint)DateTime.UtcNow.Ticks;
        response[9] = (byte)((scanId >> 24) & 0xFF);
        response[10] = (byte)((scanId >> 16) & 0xFF);
        response[11] = (byte)((scanId >> 8) & 0xFF);
        response[12] = (byte)(scanId & 0xFF);

        // T_DATA (temperature data - 2 bytes per point, signed 16-bit, in 0.1°C units)
        double baseTemp = (channel + 1) * 100.0; // Channel 0 = 100°C, Channel 1 = 200°C, etc.
        var random = new Random(channel + (int)scanId);

        for (int i = 0; i < numPoints; i++)
        {
            // Temperature with slight variation
            double temp = baseTemp + random.NextDouble() * 10 - 5;
            short tempInt = (short)(temp * 10); // Convert to 0.1°C units

            int offset = 13 + (i * 2);
            response[offset] = (byte)((tempInt >> 8) & 0xFF);
            response[offset + 1] = (byte)(tempInt & 0xFF);
        }

        // SUM (checksum of all bytes except SUM itself)
        byte checksum = 0;
        for (int i = 0; i < totalLength - 1; i++)
        {
            checksum += response[i];
        }
        response[totalLength - 1] = checksum;

        return response;
    }

    private byte[] BuildFiberBreakResponse()
    {
        // Simple fiber break response (no breaks detected)
        // Response format: FLAG2 (2) + LEN (4) + ADR (1) + CMD (1) + NUM_CH (1) + DATA + SUM
        int numChannels = _numChannels;
        int dataSize = numChannels * 5; // 5 bytes per channel
        int totalLength = 9 + dataSize + 1;
        var response = new byte[totalLength];

        response[0] = 0xCC;
        response[1] = 0xAA;

        int len = 1 + 1 + 1 + dataSize + 1;
        response[2] = (byte)((len >> 24) & 0xFF);
        response[3] = (byte)((len >> 16) & 0xFF);
        response[4] = (byte)((len >> 8) & 0xFF);
        response[5] = (byte)(len & 0xFF);

        response[6] = 0xFF; // ADR
        response[7] = 0x0D; // CMD
        response[8] = (byte)numChannels;

        // For each channel: FLAG (1) + POSITION (4)
        for (int i = 0; i < numChannels; i++)
        {
            int offset = 9 + (i * 5);
            response[offset] = 0x00; // No fiber break
            response[offset + 1] = 0x00;
            response[offset + 2] = 0x00;
            response[offset + 3] = 0x00;
            response[offset + 4] = 0x00;
        }

        // Checksum
        byte checksum = 0;
        for (int i = 0; i < totalLength - 1; i++)
        {
            checksum += response[i];
        }
        response[totalLength - 1] = checksum;

        return response;
    }

    private byte[] BuildHealthResponse(byte[] command)
    {
        // Simple health response for various internal commands
        // Return minimal valid response
        var response = new byte[31];
        response[0] = 0xCC;
        response[1] = 0xAA;
        response[2] = 0x00;
        response[3] = 0x00;
        response[4] = 0x00;
        response[5] = 0x19; // LEN
        response[6] = 0xFF; // ADR
        response[7] = 0x03; // CMD

        // Inner command response
        response[8] = 0xAA;
        response[9] = 0x55;
        response[10] = 0x13; // Inner LEN
        response[11] = 0xFF;
        response[12] = command[12]; // Echo inner CMD

        // Fill with dummy data
        for (int i = 13; i < 30; i++)
        {
            response[i] = 0x00;
        }

        // Checksum
        byte checksum = 0;
        for (int i = 0; i < 30; i++)
        {
            checksum += response[i];
        }
        response[30] = checksum;

        return response;
    }
}
