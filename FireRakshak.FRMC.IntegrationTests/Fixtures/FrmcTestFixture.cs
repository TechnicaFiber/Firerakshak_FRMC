using FireRakshak.FRMC.Core.EventAggregator;
using FireRakshak.FRMC.Core.Models;
using FireRakshak.FRMC.IntegrationTests.Mocks;
using FireRakshak.FRMC.Managers.DtsManager;
using FireRakshak.FRMC.Managers.RS232Manager;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace FireRakshak.FRMC.IntegrationTests.Fixtures;

/// <summary>
/// Test fixture providing common setup for FRMC integration tests.
/// Configures mocks for DTS, RS232, and other external dependencies.
/// </summary>
public class FrmcTestFixture : IDisposable
{
    public MockDtsManager MockDts { get; }
    public MockRS232Manager MockRS232 { get; }
    public MockEventAggregator MockEventAggregator { get; }
    public ILoggerFactory LoggerFactory { get; }

    // Default test configurations
    public SystemConfiguration DefaultSystemConfig { get; }
    public List<ChannelConfiguration> DefaultChannelConfigs { get; }
    public List<ZoneConfiguration> DefaultZoneConfigs { get; }

    public FrmcTestFixture()
    {
        MockDts = new MockDtsManager();
        MockRS232 = new MockRS232Manager();
        MockEventAggregator = new MockEventAggregator();
        LoggerFactory = new NullLoggerFactory();

        // Setup default system configuration
        DefaultSystemConfig = new SystemConfiguration(
            ipAddress: "192.168.1.100",
            subnetMask: "255.255.255.0",
            gateway: "192.168.1.1",
            systemTime: DateTime.Now,
            softwareVersion: "1.0.0",
            backgroundServiceInterval: 5000
        )
        {
            NumberOfRelays = 48,
            MaxZonesPerChannel = 20,
            DtsPollingInterval = 6000,
            AlarmEvaluationInterval = 1000,
            AlarmStatusBroadcastInterval = 1000,
            HealthStatusBroadcastInterval = 5000,
            AutoAlarmClearTimeout = 30
        };

        // Setup default channel configurations
        DefaultChannelConfigs = new List<ChannelConfiguration>();
        for (int ch = 1; ch <= 4; ch++)
        {
            DefaultChannelConfigs.Add(new ChannelConfiguration
            {
                ChannelId = ch,
                Name = $"Channel {ch}",
                ChannelLength = 1000,
                IsEnabled = ch <= 2, // Only first 2 channels enabled by default
                ScanPeriod = 6000,
                NumberOfZones = 5
            });
        }

        // Setup default zone configurations
        DefaultZoneConfigs = new List<ZoneConfiguration>();
        foreach (var channel in DefaultChannelConfigs.Where(c => c.IsEnabled))
        {
            for (int z = 1; z <= channel.NumberOfZones; z++)
            {
                DefaultZoneConfigs.Add(new ZoneConfiguration
                {
                    ChannelId = channel.ChannelId,
                    ZoneId = z,
                    Name = $"Zone {channel.ChannelId}.{z}",
                    StartPoint = (z - 1) * 200,
                    EndPoint = z * 200,
                    MaxTemp = 80,
                    MinTemp = 5,
                    PreAlarm = 60,
                    RoRThreshold = 10,
                    DeviationThreshold = 20,
                    IsMaxTempEnabled = true,
                    IsMinTempEnabled = true,
                    IsPreAlarmEnabled = true,
                    IsRateOfRiseEnabled = true,
                    IsDeviationEnabled = true,
                    Enabled = true,
                    AssignedRelayId = z <= 3 ? 11 + z : null // First 3 zones have relays (12, 13, 14)
                });
            }
        }
    }

    public ILogger<T> CreateLogger<T>()
    {
        return LoggerFactory.CreateLogger<T>();
    }

    public void Reset()
    {
        MockDts.Reset();
        MockRS232.Reset();
        MockEventAggregator.Reset();
    }

    public void Dispose()
    {
        LoggerFactory?.Dispose();
    }
}

/// <summary>
/// Collection definition for sharing test fixture across test classes.
/// </summary>
[CollectionDefinition("FRMC Integration Tests")]
public class FrmcTestCollection : ICollectionFixture<FrmcTestFixture>
{
}
