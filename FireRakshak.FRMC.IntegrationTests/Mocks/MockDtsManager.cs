using FireRakshak.FRMC.Core.Models;
using FireRakshak.FRMC.Managers.DtsManager;

namespace FireRakshak.FRMC.IntegrationTests.Mocks;

/// <summary>
/// Mock DTS Manager for integration testing.
/// Simulates DTS device behavior including temperature data, health parameters,
/// fiber breaks, and various alarm conditions.
/// </summary>
public class MockDtsManager : IDtsManager
{
    private bool _isConnected;
    private readonly Dictionary<byte, double> _channelLengths = new();
    private readonly Dictionary<byte, TemperatureDataPoint[]> _channelData = new();
    private HealthParameters _healthParameters;
    private DtsConfiguration? _configuration;

    // Point resolution (configurable to match DTS device settings)
    public double PointResolutionMeters { get; set; } = 0.4;

    // Simulation control properties
    public bool SimulateFiberBreak { get; set; }
    public double FiberBreakPosition { get; set; } = 500;
    public bool SimulateHighTemperature { get; set; }
    public double HighTemperatureValue { get; set; } = 100;
    public double HighTemperaturePosition { get; set; } = 250;
    public bool SimulateLowTemperature { get; set; }
    public double LowTemperatureValue { get; set; } = -10;
    public double LowTemperaturePosition { get; set; } = 300;
    public bool SimulateRateOfRise { get; set; }
    public double BaseTemperature { get; set; } = 25;
    public double TemperatureIncrement { get; set; } = 0;
    public bool SimulateDeviation { get; set; }
    public double DeviationValue { get; set; } = 30;

    // Event tracking for test assertions
    public int ReadTemperatureCallCount { get; private set; }
    public int ReadHealthCallCount { get; private set; }
    public List<(byte Channel, double Length)> ConfiguredChannels { get; } = new();

    public bool IsConnected => _isConnected;

    public MockDtsManager()
    {
        _healthParameters = new HealthParameters(
            lampStatus: true,
            dtsResponsive: true,
            moduleTemperature: 35.0,
            apdValue: 1.5,
            laserCurrent: 200,
            pumpCurrent: 90,
            lastCheck: DateTime.UtcNow
        );

        // Initialize default channel lengths
        for (byte ch = 1; ch <= 8; ch++)
        {
            _channelLengths[ch] = 1000; // 1000 meters default
        }
    }

    public Task ConnectAsync()
    {
        _isConnected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        _isConnected = false;
        return Task.CompletedTask;
    }

    public void ForceDisconnect()
    {
        _isConnected = false;
    }

    public Task<TemperatureDataPoint[]> ReadChannelTemperatureAsync(byte channel)
    {
        ReadTemperatureCallCount++;

        if (!_isConnected)
            throw new InvalidOperationException("DTS not connected");

        var channelLength = _channelLengths.GetValueOrDefault(channel, 1000);
        var points = GenerateTemperatureData(channel, channelLength);

        return Task.FromResult(points);
    }

    public Task<byte[]?> ReadChannelRawDataAsync(byte channel)
    {
        ReadTemperatureCallCount++;

        if (!_isConnected)
            return Task.FromResult<byte[]?>(null);

        // Generate raw DTS response format that can be parsed by DtsDataPipeline
        var channelLength = _channelLengths.GetValueOrDefault(channel, 1000);
        var rawResponse = GenerateRawDtsResponse(channel, channelLength);

        return Task.FromResult<byte[]?>(rawResponse);
    }

    private byte[] GenerateRawDtsResponse(byte channel, double length)
    {
        double resolution = PointResolutionMeters;
        const int discardCount = 475;

        var pointCount = (int)(length / resolution);
        var totalSamples = discardCount + pointCount;

        // Response format: FLAG2(2) + LEN(4) + ADR(1) + CMD(1) + CH(1) + ID(4) + T_DATA(N*2) + SUM(1)
        // Minimum header = 14 bytes, plus temperature data
        var dataLength = totalSamples * 2;
        var totalLength = 14 + dataLength;
        var response = new byte[totalLength];

        // FLAG2
        response[0] = 0xCC;
        response[1] = 0xAA;

        // LEN (4 bytes, big-endian) - length after FLAG2
        var len = totalLength - 6;
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

        // ID (4 bytes)
        response[9] = 0x00;
        response[10] = 0x00;
        response[11] = 0x00;
        response[12] = 0x01;

        // Temperature data starting at index 13
        var currentBase = BaseTemperature + TemperatureIncrement;

        for (int i = 0; i < totalSamples; i++)
        {
            double position = (i - discardCount) * resolution;
            double temperature = currentBase;

            if (i >= discardCount) // Only apply effects to valid data
            {
                if (SimulateHighTemperature && Math.Abs(position - HighTemperaturePosition) < 10)
                {
                    temperature = HighTemperatureValue;
                }
                else if (SimulateLowTemperature && Math.Abs(position - LowTemperaturePosition) < 10)
                {
                    temperature = LowTemperatureValue;
                }
                else
                {
                    temperature += (Random.Shared.NextDouble() - 0.5) * 2;
                }
            }

            // Convert to raw format (temperature * 10, 16-bit signed big-endian)
            short rawTemp = (short)(temperature * 10);
            int dataIndex = 13 + (i * 2);
            response[dataIndex] = (byte)((rawTemp >> 8) & 0xFF);
            response[dataIndex + 1] = (byte)(rawTemp & 0xFF);
        }

        // Calculate checksum (sum of all bytes except checksum itself)
        byte checksum = 0;
        for (int i = 0; i < totalLength - 1; i++)
        {
            checksum += response[i];
        }
        response[totalLength - 1] = checksum;

        return response;
    }

    private TemperatureDataPoint[] GenerateTemperatureData(byte channel, double length)
    {
        double resolution = PointResolutionMeters; // meters per point
        var pointCount = (int)(length / resolution);
        var points = new TemperatureDataPoint[pointCount];

        // Apply temperature increment for RoR simulation
        var currentBase = BaseTemperature + TemperatureIncrement;

        for (int i = 0; i < pointCount; i++)
        {
            double position = i * resolution;
            double temperature = currentBase;

            // Simulate fiber break - no valid data beyond break position
            if (SimulateFiberBreak && position > FiberBreakPosition)
            {
                temperature = -999; // Invalid/no signal
            }
            // Simulate high temperature alarm
            else if (SimulateHighTemperature &&
                     Math.Abs(position - HighTemperaturePosition) < 10)
            {
                temperature = HighTemperatureValue;
            }
            // Simulate low temperature alarm
            else if (SimulateLowTemperature &&
                     Math.Abs(position - LowTemperaturePosition) < 10)
            {
                temperature = LowTemperatureValue;
            }
            // Simulate deviation (hot spot)
            else if (SimulateDeviation &&
                     Math.Abs(position - HighTemperaturePosition) < 5)
            {
                temperature = currentBase + DeviationValue;
            }
            else
            {
                // Add some noise for realism
                temperature += (Random.Shared.NextDouble() - 0.5) * 2;
            }

            points[i] = new TemperatureDataPoint(temperature, position);
        }

        return points;
    }

    public Task<HealthParameters> ReadHealthParametersAsync()
    {
        ReadHealthCallCount++;

        if (!_isConnected)
            throw new InvalidOperationException("DTS not connected");

        _healthParameters = new HealthParameters(
            _healthParameters.LampStatus,
            _healthParameters.DtsResponsive,
            _healthParameters.ModuleTemperature,
            _healthParameters.ApdValue,
            _healthParameters.LaserCurrent,
            _healthParameters.PumpCurrent,
            DateTime.UtcNow
        );

        return Task.FromResult(_healthParameters);
    }

    public Task ConfigureChannelLengthAsync(byte channel, double length)
    {
        _channelLengths[channel] = length;
        ConfiguredChannels.Add((channel, length));
        return Task.CompletedTask;
    }

    public Task<bool> ConfigureDtsParametersAsync(DtsConfiguration config)
    {
        _configuration = config;
        return Task.FromResult(true);
    }

    public Task<bool> ConfigureFromJsonAsync()
    {
        return Task.FromResult(true);
    }

    // Test helper methods
    public void SetHealthParameters(HealthParameters parameters)
    {
        _healthParameters = parameters;
    }

    public void SetLampStatus(bool status)
    {
        _healthParameters = new HealthParameters(
            status,
            _healthParameters.DtsResponsive,
            _healthParameters.ModuleTemperature,
            _healthParameters.ApdValue,
            _healthParameters.LaserCurrent,
            _healthParameters.PumpCurrent,
            DateTime.UtcNow
        );
    }

    public void SetDtsResponsive(bool responsive)
    {
        _healthParameters = new HealthParameters(
            _healthParameters.LampStatus,
            responsive,
            _healthParameters.ModuleTemperature,
            _healthParameters.ApdValue,
            _healthParameters.LaserCurrent,
            _healthParameters.PumpCurrent,
            DateTime.UtcNow
        );
    }

    public void SetPumpCurrent(double pumpCurrent)
    {
        _healthParameters = new HealthParameters(
            _healthParameters.LampStatus,
            _healthParameters.DtsResponsive,
            _healthParameters.ModuleTemperature,
            _healthParameters.ApdValue,
            _healthParameters.LaserCurrent,
            pumpCurrent,
            DateTime.UtcNow
        );
    }

    public void Reset()
    {
        _isConnected = false; // Reset connection state
        ReadTemperatureCallCount = 0;
        ReadHealthCallCount = 0;
        ConfiguredChannels.Clear();

        // Reset simulation flags
        SimulateFiberBreak = false;
        SimulateHighTemperature = false;
        SimulateLowTemperature = false;
        SimulateRateOfRise = false;
        SimulateDeviation = false;

        // Reset simulation values to defaults
        FiberBreakPosition = 500;
        HighTemperatureValue = 100;
        HighTemperaturePosition = 250;
        LowTemperatureValue = -10;
        LowTemperaturePosition = 300;
        DeviationValue = 30;
        TemperatureIncrement = 0;
        BaseTemperature = 25;
        PointResolutionMeters = 0.4;

        // Reset channel lengths to default
        for (byte ch = 1; ch <= 8; ch++)
        {
            _channelLengths[ch] = 1000;
        }

        // Reset health parameters to default values
        _healthParameters = new HealthParameters(
            lampStatus: true,
            dtsResponsive: true,
            moduleTemperature: 35.0,
            apdValue: 1.5,
            laserCurrent: 200,
            pumpCurrent: 90,
            lastCheck: DateTime.UtcNow
        );
    }
}
