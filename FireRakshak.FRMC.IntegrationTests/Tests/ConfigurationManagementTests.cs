using FireRakshak.FRMC.Core.Enums;
using FireRakshak.FRMC.Core.Events;
using FireRakshak.FRMC.Core.Models;
using FireRakshak.FRMC.IntegrationTests.Fixtures;
using FluentAssertions;
using Xunit;

namespace FireRakshak.FRMC.IntegrationTests.Tests;

/// <summary>
/// Integration tests for configuration management.
/// Tests zone, channel, and system configuration operations.
/// </summary>
[Collection("FRMC Integration Tests")]
public class ConfigurationManagementTests
{
    private readonly FrmcTestFixture _fixture;

    public ConfigurationManagementTests(FrmcTestFixture fixture)
    {
        _fixture = fixture;
        _fixture.Reset();
    }

    [Fact]
    public void SystemConfiguration_DefaultValues_AreValid()
    {
        // Arrange & Act
        var config = _fixture.DefaultSystemConfig;

        // Assert
        config.Should().NotBeNull();
        config.NumberOfRelays.Should().Be(48);
        config.MaxZonesPerChannel.Should().Be(20);
        config.DtsPollingInterval.Should().BeGreaterThan(0);
        config.AlarmEvaluationInterval.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ChannelConfiguration_DefaultChannels_AreConfigured()
    {
        // Arrange & Act
        var channels = _fixture.DefaultChannelConfigs;

        // Assert
        channels.Should().HaveCount(4);
        channels.Count(c => c.IsEnabled).Should().Be(2, "Only first 2 channels enabled by default");
        channels.All(c => c.ChannelLength > 0).Should().BeTrue();
        channels.All(c => c.ScanPeriod > 0).Should().BeTrue();
    }

    [Fact]
    public void ZoneConfiguration_DefaultZones_AreConfigured()
    {
        // Arrange & Act
        var zones = _fixture.DefaultZoneConfigs;

        // Assert
        zones.Should().NotBeEmpty();
        zones.All(z => z.EndPoint > z.StartPoint).Should().BeTrue("EndPoint must be after StartPoint");
        zones.All(z => z.MaxTemp > z.PreAlarm).Should().BeTrue("MaxTemp must be above PreAlarm");
        zones.All(z => z.MaxTemp > z.MinTemp).Should().BeTrue("MaxTemp must be above MinTemp");
    }

    [Fact]
    public void ZoneConfiguration_RelayAssignment_IsValid()
    {
        // Arrange
        var zones = _fixture.DefaultZoneConfigs;

        // Act
        var zonesWithRelays = zones.Where(z => z.AssignedRelayId.HasValue).ToList();

        // Assert
        zonesWithRelays.Should().NotBeEmpty("Some zones should have relays assigned");
        zonesWithRelays.All(z => z.AssignedRelayId >= 11 && z.AssignedRelayId <= 48)
            .Should().BeTrue("Assignable relays are 11-48");
    }

    [Fact]
    public void ZoneConfiguration_ThresholdsEnabled_MatchConfiguration()
    {
        // Arrange
        var zone = _fixture.DefaultZoneConfigs.First();

        // Assert
        zone.IsMaxTempEnabled.Should().BeTrue();
        zone.IsMinTempEnabled.Should().BeTrue();
        zone.IsPreAlarmEnabled.Should().BeTrue();
        zone.IsRateOfRiseEnabled.Should().BeTrue();
        zone.IsDeviationEnabled.Should().BeTrue();
    }

    [Fact]
    public void ZoneConfiguration_ZonesPerChannel_DoNotExceedMax()
    {
        // Arrange
        var maxZonesPerChannel = _fixture.DefaultSystemConfig.MaxZonesPerChannel;
        var zones = _fixture.DefaultZoneConfigs;

        // Act
        var zonesPerChannel = zones.GroupBy(z => z.ChannelId)
            .Select(g => new { ChannelId = g.Key, Count = g.Count() });

        // Assert
        zonesPerChannel.All(c => c.Count <= maxZonesPerChannel)
            .Should().BeTrue($"No channel should have more than {maxZonesPerChannel} zones");
    }

    [Fact]
    public void ZoneConfiguration_ZonesDoNotOverlap_WithinChannel()
    {
        // Arrange
        var zones = _fixture.DefaultZoneConfigs;

        // Act & Assert
        foreach (var channelGroup in zones.GroupBy(z => z.ChannelId))
        {
            var sortedZones = channelGroup.OrderBy(z => z.StartPoint).ToList();

            for (int i = 0; i < sortedZones.Count - 1; i++)
            {
                var currentZone = sortedZones[i];
                var nextZone = sortedZones[i + 1];

                currentZone.EndPoint.Should().BeLessThanOrEqualTo(nextZone.StartPoint,
                    $"Zone {currentZone.ZoneId} should not overlap with Zone {nextZone.ZoneId}");
            }
        }
    }

    [Fact]
    public void ChannelConfiguration_ChannelIds_AreUnique()
    {
        // Arrange
        var channels = _fixture.DefaultChannelConfigs;

        // Act
        var uniqueIds = channels.Select(c => c.ChannelId).Distinct().Count();

        // Assert
        uniqueIds.Should().Be(channels.Count, "All channel IDs should be unique");
    }

    [Fact]
    public void ZoneConfiguration_ZoneIds_AreUniquePerChannel()
    {
        // Arrange
        var zones = _fixture.DefaultZoneConfigs;

        // Act & Assert
        foreach (var channelGroup in zones.GroupBy(z => z.ChannelId))
        {
            var zoneIds = channelGroup.Select(z => z.ZoneId).ToList();
            var uniqueZoneIds = zoneIds.Distinct().Count();

            uniqueZoneIds.Should().Be(zoneIds.Count,
                $"Zone IDs should be unique within channel {channelGroup.Key}");
        }
    }

    [Fact]
    public void SystemConfiguration_BroadcastIntervals_AreReasonable()
    {
        // Arrange
        var config = _fixture.DefaultSystemConfig;

        // Assert
        config.AlarmStatusBroadcastInterval.Should().BeInRange(500, 5000,
            "Alarm broadcast should be 0.5-5 seconds");
        config.HealthStatusBroadcastInterval.Should().BeInRange(1000, 60000,
            "Health broadcast should be 1-60 seconds");
    }

    [Fact]
    public void SystemConfiguration_AutoAlarmClearTimeout_IsReasonable()
    {
        // Arrange
        var config = _fixture.DefaultSystemConfig;

        // Assert
        config.AutoAlarmClearTimeout.Should().BeGreaterThan(0);
        config.AutoAlarmClearTimeout.Should().BeLessThanOrEqualTo(300,
            "Auto-clear timeout should not exceed 5 minutes");
    }

    [Fact]
    public void ZoneConfiguration_RoRThreshold_IsReasonable()
    {
        // Arrange
        var zones = _fixture.DefaultZoneConfigs;

        // Assert
        zones.All(z => z.RoRThreshold > 0 && z.RoRThreshold <= 50)
            .Should().BeTrue("RoR threshold should be 0-50°C/minute");
    }

    [Fact]
    public void ZoneConfiguration_DeviationThreshold_IsReasonable()
    {
        // Arrange
        var zones = _fixture.DefaultZoneConfigs;

        // Assert
        zones.All(z => z.DeviationThreshold > 0 && z.DeviationThreshold <= 100)
            .Should().BeTrue("Deviation threshold should be 0-100°C");
    }
}
