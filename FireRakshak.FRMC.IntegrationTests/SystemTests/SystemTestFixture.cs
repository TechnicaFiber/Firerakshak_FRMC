using FireRakshak.FRMC.Core.EventAggregator;
using FireRakshak.FRMC.Core.Models;
using FireRakshak.FRMC.IntegrationTests.Mocks;
using FireRakshak.FRMC.Managers.DtsManager;
using FireRakshak.FRMC.Managers.RS232Manager;
using FireRakshak.FRMC.Managers.SystemConfigurationManager;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace FireRakshak.FRMC.IntegrationTests.SystemTests;

/// <summary>
/// System test fixture that uses REAL EventAggregator with mocked hardware.
/// This enables testing of the event-driven architecture with production code.
/// </summary>
public class SystemTestFixture : IDisposable
{
    // Mocks for hardware boundaries
    public MockDtsManager MockDts { get; }
    public MockRS232Manager MockRS232 { get; }

    // REAL production EventAggregator
    public IEventAggregator EventAggregator { get; }

    // Mock for configuration
    public Mock<IConfigurationManager> MockConfigManager { get; }

    // Configuration
    public SystemConfiguration SystemConfig { get; }
    public List<ChannelConfiguration> ChannelConfigs { get; }
    public List<ZoneConfiguration> ZoneConfigs { get; }

    private readonly ILoggerFactory _loggerFactory;

    public SystemTestFixture()
    {
        // Create mocks for hardware
        MockDts = new MockDtsManager();
        MockRS232 = new MockRS232Manager();

        // Setup configurations
        SystemConfig = CreateDefaultSystemConfig();
        ChannelConfigs = CreateDefaultChannelConfigs();
        ZoneConfigs = CreateDefaultZoneConfigs();

        // Create logger factory
        _loggerFactory = new NullLoggerFactory();

        // Create REAL EventAggregator (production code)
        EventAggregator = new EventAggregator(_loggerFactory.CreateLogger<EventAggregator>());

        // Setup configuration manager mock
        MockConfigManager = new Mock<IConfigurationManager>();
        MockConfigManager.Setup(m => m.GetSystemConfigurationAsync())
            .ReturnsAsync(SystemConfig);
        MockConfigManager.Setup(m => m.GetZoneConfigurations())
            .Returns(ZoneConfigs);
        MockConfigManager.Setup(m => m.GetAllChannelConfigurationsAsync())
            .ReturnsAsync(ChannelConfigs);

        // Setup individual zone config retrieval
        foreach (var zone in ZoneConfigs)
        {
            MockConfigManager.Setup(m => m.GetZoneConfigAsync(zone.ChannelId, zone.ZoneId))
                .ReturnsAsync(zone);
        }
    }

    private SystemConfiguration CreateDefaultSystemConfig()
    {
        return new SystemConfiguration(
            ipAddress: "192.168.1.100",
            subnetMask: "255.255.255.0",
            gateway: "192.168.1.1",
            systemTime: DateTime.Now,
            softwareVersion: "1.0.0",
            backgroundServiceInterval: 5000)
        {
            NumberOfRelays = 48,
            MaxZonesPerChannel = 20,
            DtsPollingInterval = 6000,
            AlarmEvaluationInterval = 1000,
            AlarmStatusBroadcastInterval = 1000,
            HealthStatusBroadcastInterval = 5000,
            AutoAlarmClearTimeout = 30
        };
    }

    private List<ChannelConfiguration> CreateDefaultChannelConfigs()
    {
        var configs = new List<ChannelConfiguration>();
        for (int ch = 1; ch <= 4; ch++)
        {
            configs.Add(new ChannelConfiguration
            {
                ChannelId = ch,
                Name = $"Channel {ch}",
                ChannelLength = 1000,
                IsEnabled = ch <= 2,
                ScanPeriod = 6000,
                NumberOfZones = 5
            });
        }
        return configs;
    }

    private List<ZoneConfiguration> CreateDefaultZoneConfigs()
    {
        var configs = new List<ZoneConfiguration>();
        foreach (var channel in ChannelConfigs.Where(c => c.IsEnabled))
        {
            for (int z = 1; z <= channel.NumberOfZones; z++)
            {
                configs.Add(new ZoneConfiguration
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
                    AssignedRelayId = 10 + ((channel.ChannelId - 1) * 5) + z
                });
            }
        }
        return configs;
    }

    public ILogger<T> CreateLogger<T>()
    {
        return _loggerFactory.CreateLogger<T>();
    }

    public ZoneConfiguration GetZoneConfig(int channelId, int zoneId)
    {
        return ZoneConfigs.First(z => z.ChannelId == channelId && z.ZoneId == zoneId);
    }

    public void Reset()
    {
        MockDts.Reset();
        MockRS232.Reset();
    }

    public void Dispose()
    {
        _loggerFactory?.Dispose();
    }
}

/// <summary>
/// Collection definition for system tests.
/// </summary>
[CollectionDefinition("System Integration Tests")]
public class SystemTestCollection : ICollectionFixture<SystemTestFixture>
{
}
