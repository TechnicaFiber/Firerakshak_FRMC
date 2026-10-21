using FireRakshak.FRMC.Core.Enums;
using FireRakshak.FRMC.Core.EventAggregator;
using FireRakshak.FRMC.Core.Events;
using FireRakshak.FRMC.Core.Models;
using FireRakshak.FRMC.Host;
using FireRakshak.FRMC.Infrastructure.Logging;
using FireRakshak.FRMC.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FireRakshak.FRMC.Managers.SystemConfigurationManager
{
    public class ConfigurationManager : IConfigurationManager
    {
        private readonly ILogger<ConfigurationManager> _logger;
        private readonly IEventAggregator _eventAggregator;
        private readonly FileSystemPersistence _fileSystemPersistence;
        private readonly AuditTrailService _auditTrailService;
        private readonly IOptions<List<ChannelConfiguration>> _defaultChannelConfigurations;
        private readonly WebSocketServer _webSocketServiceServer;

        private readonly ConcurrentDictionary<int, Channel> _channels =
            new ConcurrentDictionary<int, Channel>();

        private SystemConfiguration? _systemConfiguration;

        // ADDED: Semaphore to prevent concurrent configuration saves
        private readonly SemaphoreSlim _configSaveLock = new SemaphoreSlim(1, 1);

        private const string ConfigFileName = "system_configurations.json";
        private const string SystemConfigFileName = "system_config.json";
        private const string ConfigFolderPath = "Config";
        private static readonly string EthConfigPath =
        Path.Combine(AppContext.BaseDirectory, "ethconfig.json");

        private EthernetConfigFile? _ethernetConfig;

        public ConfigurationManager(
            ILogger<ConfigurationManager> logger,
            IEventAggregator eventAggregator,
            FileSystemPersistence fileSystemPersistence,
            AuditTrailService auditTrailService,
            IOptions<List<ChannelConfiguration>> defaultChannelConfigurations)
        {
            _logger = logger;
            _eventAggregator = eventAggregator;
            _fileSystemPersistence = fileSystemPersistence;
            _auditTrailService = auditTrailService;
            _defaultChannelConfigurations = defaultChannelConfigurations;
        }



        public List<Channel> GetChannels()
        {
            return _channels.Values
                .OrderBy(c => c.Configuration.ChannelId)
                .ToList();
        }

        public Task<List<ChannelConfiguration>> GetAllChannelConfigurationsAsync()
        {
            var configs = _channels.Values
                .OrderBy(c => c.Configuration.ChannelId)
                .Select(c => c.Configuration)
                .ToList();

            return Task.FromResult(configs);
        }

        public Task<ChannelConfiguration?> GetChannelConfigurationAsync(int channelId)
        {
            if (_channels.TryGetValue(channelId, out var channel))
                return Task.FromResult<ChannelConfiguration?>(channel.Configuration);

            return Task.FromResult<ChannelConfiguration?>(null);
        }

        /// <summary>
        /// FIXED: Added transaction semantics with rollback on save failure.
        /// </summary>
        public async Task AddOrUpdateChannelConfigurationAsync(ChannelConfiguration config, string changedBy = "Admin")
        {
            await _configSaveLock.WaitAsync();
            try
            {
                // 1. Save old value for rollback
                Channel? existingChannel = null;
                ChannelConfiguration? oldConfig = null;
                bool isUpdate = _channels.TryGetValue(config.ChannelId, out existingChannel);

                if (isUpdate && existingChannel != null)
                {
                    oldConfig = existingChannel.Configuration; // Save for rollback
                }

                // 2. Update in-memory state
                if (isUpdate && existingChannel != null)
                {
                    existingChannel.Configuration = config;
                    await _auditTrailService.LogActivityAsync(
                        $"Channel {config.ChannelId} configuration updated.", changedBy);
                }
                else
                {
                    var newChannel = new Channel(config);
                    if (_channels.TryAdd(config.ChannelId, newChannel))
                    {
                        await _auditTrailService.LogActivityAsync(
                            $"Channel {config.ChannelId} added.", changedBy);
                    }
                    else
                    {
                        _logger.LogError("Failed to add channel {ChannelId} to dictionary", config.ChannelId);
                        throw new InvalidOperationException($"Failed to add channel {config.ChannelId}");
                    }
                }

                // 3. Try to save to disk
                try
                {
                    await SaveConfigurationsAsync();
                }
                catch (Exception saveEx)
                {
                    // 4. Rollback on save failure
                    _logger.LogError(saveEx, "Failed to save configuration, rolling back channel {ChannelId}", config.ChannelId);

                    if (isUpdate && oldConfig != null && existingChannel != null)
                    {
                        // Restore old configuration
                        existingChannel.Configuration = oldConfig;
                        _logger.LogInformation("Rolled back channel {ChannelId} to previous configuration", config.ChannelId);
                    }
                    else if (!isUpdate)
                    {
                        // Remove newly added channel
                        _channels.TryRemove(config.ChannelId, out _);
                        _logger.LogInformation("Removed newly added channel {ChannelId} due to save failure", config.ChannelId);
                    }

                    throw; // Re-throw so caller knows save failed
                }
            }
            finally
            {
                _configSaveLock.Release();
            }
        }

        public async Task<bool> UpdateChannelConfigurationAsync(ChannelConfiguration channelConfig, string changedBy = "Admin")
        {
            if (channelConfig == null)
                return false;

            return await AddOrUpdateChannelConfigurationAsyncInternal(channelConfig, changedBy);
        }

        private async Task<bool> AddOrUpdateChannelConfigurationAsyncInternal(ChannelConfiguration config, string changedBy = "Admin")
        {
            bool isNewChannel = false;

            if (_channels.TryGetValue(config.ChannelId, out var existing))
            {
                existing.Configuration = config;
                await SaveConfigurationsAsync();
            }
            else
            {
                var newChannel = new Channel(config);
                if (_channels.TryAdd(config.ChannelId, newChannel))
                {
                    isNewChannel = true;
                    await SaveConfigurationsAsync();
                }
                else
                {
                    return false;
                }
            }

            // Publish ConfigurationChangedEvent to notify Modbus server and other subscribers
            await _eventAggregator.PublishAsync(
                new ConfigurationChangedEvent(
                    ConfigurationType.Channel,
                    changedBy,
                    "ConfigurationManager",
                    new Dictionary<string, object>
                    {
                        { "ChannelId", config.ChannelId },
                        { "IsNew", isNewChannel },
                        { "Action", isNewChannel ? "Added" : "Updated" }
                    }
                )
            );

            _logger.LogInformation("Channel {ChId} configuration {Action} by {User}, event published",
                config.ChannelId, isNewChannel ? "added" : "updated", changedBy);

            return true;
        }

        /// <summary>
        /// DEPRECATED: Runtime state is no longer persisted to configuration file.
        /// State is only saved to EventHistory when alarms are triggered/cleared.
        /// This method now only updates in-memory state.
        /// </summary>
        public Task UpdateChannelStateAsync(int channelId, ChannelState state)
        {
            if (_webSocketServiceServer.IsStreamingActive)
            {
                if (_channels.TryGetValue(channelId, out var channel))
                    {
                        channel.State = state;
                        // NOTE: State is no longer saved to disk here
                        // Only configuration changes are persisted to system_configurations.json
                    }
            }
            
            return Task.CompletedTask;
        }


        /// <summary>
        /// Returns ALL zones from ALL channels
        /// </summary>
        public List<ZoneConfiguration> GetZoneConfigurations()
        {
            return _channels.Values
                .SelectMany(ch => ch.Zones.Select(z => z.Configuration))
                .ToList();
        }

        /// <summary>
        /// Gets a specific zone configuration by channel and zone ID
        /// </summary>
        public Task<ZoneConfiguration?> GetZoneConfigAsync(int channelId, int zoneId)
        {
            if (_channels.TryGetValue(channelId, out var channel))
            {
                var zone = channel.Zones.FirstOrDefault(z => z.Configuration.ZoneId == zoneId);
                return Task.FromResult(zone?.Configuration);
            }

            return Task.FromResult<ZoneConfiguration?>(null);
        }

        /// <summary>
        /// Replaces zone configuration for a channel.
        /// FIXED: Now supports sparse zone configurations - zones can have non-sequential IDs.
        /// Deleted zones are permanently removed, not recreated as disabled placeholders.
        /// </summary>
        public async Task<bool> ReplaceZoneConfigurationAsync(int channelId, List<ZoneConfiguration> newZones, string changedBy = "Admin")
        {
            if (!_channels.TryGetValue(channelId, out var channel))
            {
                _logger.LogWarning("Channel {ChannelId} not found.", channelId);
                return false;
            }

            // FIXED: Accept zones exactly as provided by DTSCM
            // No filtering, no auto-creation of missing zones
            // Zone IDs can be sparse (e.g., Zone 1, Zone 3, Zone 5 is valid)
            var trimmedZones = newZones
                .OrderBy(z => z.ZoneId)
                .ToList();

            // Validate that all zones belong to this channel
            foreach (var zone in trimmedZones)
            {
                if (zone.ChannelId != channelId)
                {
                    _logger.LogWarning("Zone {ZoneId} has mismatched ChannelId {ZoneChannelId}, expected {ChannelId}",
                        zone.ZoneId, zone.ChannelId, channelId);
                    zone.ChannelId = channelId; // Fix the mismatch
                }
            }

            // Detect added and deleted zones for audit logging
            var oldZoneIds = channel.Zones.Select(z => z.Configuration.ZoneId).ToHashSet();
            var newZoneIds = trimmedZones.Select(z => z.ZoneId).ToHashSet();
            var addedZoneIds = newZoneIds.Except(oldZoneIds).ToList();
            var deletedZones = channel.Zones.Where(z => !newZoneIds.Contains(z.Configuration.ZoneId)).ToList();

            channel.Zones = trimmedZones
                .Select(z => new Zone
                {
                    Configuration = z,
                    State = new ZoneState(
                        z.ZoneId,
                        z.Name ?? $"Zone {z.ZoneId}",
                        z.StartPoint,  // Use configuration values
                        z.EndPoint,    // Use configuration values
                        0,  // AverageTemperature - starts at 0 until DTS data arrives
                        0,  // MaxTemperature
                        0,  // MinTemperature
                        false,  // PreAlarm
                        false,  // MaxAlarm
                        false,  // MinAlarm
                        DateTime.UtcNow
                    )
                    {
                        Enabled = z.Enabled  // Use configuration value
                    }
                })
                .ToList();

            await SaveConfigurationsAsync();

            // Log audit trail - only log meaningful changes
            // 1. Log zones added (single summary entry)
            if (addedZoneIds.Count > 0)
            {
                await _auditTrailService.LogZonesAddedAsync(changedBy, channelId, addedZoneIds.Count);
            }

            // 2. Log each deleted zone individually
            foreach (var deletedZone in deletedZones)
            {
                await _auditTrailService.LogZoneDeletedAsync(
                    changedBy,
                    channelId,
                    deletedZone.Configuration.ZoneId,
                    deletedZone.Configuration.Name);
            }

            // Publish ConfigurationChangedEvent to notify Modbus server and other subscribers
            await _eventAggregator.PublishAsync(
                new ConfigurationChangedEvent(
                    ConfigurationType.Zone,
                    changedBy,
                    "ConfigurationManager",
                    new Dictionary<string, object>
                    {
                        { "ChannelId", channelId },
                        { "ZoneCount", channel.Zones.Count },
                        { "Action", "ZonesReplaced" }
                    }
                )
            );

            _logger.LogInformation("Channel {ChId} zones replaced by {User}: {Count} zones with IDs [{ZoneIds}], event published",
                channelId, changedBy, channel.Zones.Count, string.Join(", ", trimmedZones.Select(z => z.ZoneId)));

            return true;
        }


        /// <summary>
        /// Update a single zone configuration without affecting other zones
        /// </summary>
        public async Task<bool> UpdateSingleZoneAsync(int channelId, int zoneId, ZoneConfiguration newZoneConfig, string changedBy = "Admin")
        {
            if (!_channels.TryGetValue(channelId, out var channel))
            {
                _logger.LogWarning("Channel {ChannelId} not found.", channelId);
                return false;
            }

            // Find existing zone
            var existingZone = channel.Zones.FirstOrDefault(z => z.Configuration.ZoneId == zoneId);

            if (existingZone != null)
            {
                // Update existing zone
                existingZone.Configuration = newZoneConfig;
                existingZone.State.Name = newZoneConfig.Name;  // ✅ Update zone name in state
                existingZone.State.Enabled = newZoneConfig.Enabled;
                existingZone.State.StartPoint = newZoneConfig.StartPoint;
                existingZone.State.EndPoint = newZoneConfig.EndPoint;
            }
            else
            {
                // Add new zone
                channel.Zones.Add(new Zone
                {
                    Configuration = newZoneConfig,
                    State = new ZoneState(
                        newZoneConfig.ZoneId,
                        newZoneConfig.Name ?? $"Zone {newZoneConfig.ZoneId}",
                        newZoneConfig.StartPoint,
                        newZoneConfig.EndPoint,
                        0,  // AverageTemperature
                        0,  // MaxTemperature
                        0,  // MinTemperature
                        false,  // PreAlarm
                        false,  // MaxAlarm
                        false,  // MinAlarm
                        DateTime.UtcNow
                    )
                    {
                        Enabled = newZoneConfig.Enabled
                    }
                });
            }

            await _auditTrailService.LogActivityAsync(
                $"Channel {channelId}, Zone {zoneId} updated individually.", changedBy);

            await SaveConfigurationsAsync();

            // Publish ConfigurationChangedEvent
            await _eventAggregator.PublishAsync(
                new ConfigurationChangedEvent(
                    ConfigurationType.Zone,
                    changedBy,
                    "ConfigurationManager",
                    new Dictionary<string, object>
                    {
                        { "ChannelId", channelId },
                        { "ZoneId", zoneId },
                        { "Action", existingZone != null ? "Updated" : "Added" }
                    }
                )
            );

            _logger.LogInformation("Channel {ChId}, Zone {ZoneId} updated individually by {User}, event published",
                channelId, zoneId, changedBy);

            return true;
        }

        /// <summary>
        /// Batch update — called when DTSCM sends full zone list
        /// </summary>

        public async Task<bool> RemoveZoneConfigurationAsync(int channelId, int zoneId, string changedBy = "Admin")
        {
            if (!_channels.TryGetValue(channelId, out var channel))
                return false;

            var zone = channel.Zones.FirstOrDefault(z => z.Configuration.ZoneId == zoneId);
            if (zone == null)
                return false;

            var zoneName = zone.Configuration.Name;
            channel.Zones.Remove(zone);
            await SaveConfigurationsAsync();

            // Log only this specific zone deletion
            await _auditTrailService.LogZoneDeletedAsync(changedBy, channelId, zoneId, zoneName);

            return true;
        }

        /// <summary>
        /// DEPRECATED: Runtime state is no longer persisted to configuration file.
        /// State is only saved to EventHistory when alarms are triggered/cleared.
        /// This method now only updates in-memory state.
        /// </summary>
        public Task UpdateZoneStateAsync(int channelId, int zoneId, ZoneState state)
        {
            if (!_channels.TryGetValue(channelId, out var channel))
                return Task.CompletedTask;

            var zone = channel.Zones.FirstOrDefault(z => z.Configuration.ZoneId == zoneId);
            if (zone == null)
                return Task.CompletedTask;
            if (_webSocketServiceServer.IsStreamingActive)
            {
                zone.State = state;
            }                           

            // NOTE: State is no longer saved to disk here
            // Only configuration changes are persisted to system_configurations.json
            // Runtime state is saved to EventHistory on alarm events
            return Task.CompletedTask;
        }

        // ---------------------------------------------------------
        // SYSTEM CONFIGURATION
        // ---------------------------------------------------------

        //public Task<SystemConfiguration> GetSystemConfigurationAsync()
        //{
        //    if (_systemConfiguration == null)
        //    {
        //        // Return default configuration if not loaded
        //        _systemConfiguration = new SystemConfiguration(
        //            ipAddress: "192.168.1.100",
        //            subnetMask: "255.255.255.0",
        //            gateway: "192.168.1.1",
        //            systemTime: DateTime.Now,
        //            softwareVersion: "0.1",
        //            backgroundServiceInterval: 5000
        //        );
        //    }

        //    return Task.FromResult(_systemConfiguration);
        //}
        public async Task<SystemConfiguration> GetSystemConfigurationAsync()
        {
            if (_systemConfiguration == null)
            {
                _logger.LogWarning("System configuration not loaded, attempting to load from file...");

                await LoadSystemConfigurationAsync();
            }
            return _systemConfiguration;
        }

        public async Task<bool> UpdateSystemConfigurationAsync(SystemConfiguration config, string changedBy = "Admin")
        {
            _logger.LogInformation("Updating system configuration");

            // Store old values for audit trail
            var oldConfig = _systemConfiguration;

            // FIX Bug #10: Validate relay count reduction at configuration level (not just WebSocket)
            if (oldConfig != null && config.NumberOfRelays < oldConfig.NumberOfRelays)
            {
                // Check if any zones are assigned to relays that would be removed
                var maxAssignedRelayId = GetMaxAssignedRelayId();

                if (maxAssignedRelayId > config.NumberOfRelays)
                {
                    _logger.LogWarning(
                        "Cannot reduce relay count from {OldCount} to {NewCount}: " +
                        "Relay {MaxAssignedRelayId} is still assigned to a zone. " +
                        "Unassign relays {NewCount}+ first.",
                        oldConfig.NumberOfRelays,
                        config.NumberOfRelays,
                        maxAssignedRelayId);

                    // Return false instead of throwing - let caller handle gracefully
                    return false;
                }

                _logger.LogInformation(
                    "Relay count reduction validated: {OldCount} -> {NewCount} (max assigned relay: {MaxAssigned})",
                    oldConfig.NumberOfRelays,
                    config.NumberOfRelays,
                    maxAssignedRelayId);
            }

            // Update to new config
            _systemConfiguration = config;

            // Save to file
            await SaveSystemConfigurationAsync();

            // Log each changed parameter to audit trail
            if (oldConfig != null)
            {
                string username = changedBy;
                if (oldConfig.NumberOfRelays != config.NumberOfRelays)
                {
                    await _auditTrailService.LogConfigurationChangeAsync(
                        ConfigurationType.System, username, "NumberOfRelays",
                        oldConfig.NumberOfRelays, config.NumberOfRelays);
                }

                if (oldConfig.MaxZonesPerChannel != config.MaxZonesPerChannel)
                {
                    await _auditTrailService.LogConfigurationChangeAsync(
                        ConfigurationType.System, username, "MaxZonesPerChannel",
                        oldConfig.MaxZonesPerChannel, config.MaxZonesPerChannel);
                }

                if (oldConfig.DtsPollingInterval != config.DtsPollingInterval)
                {
                    await _auditTrailService.LogConfigurationChangeAsync(
                        ConfigurationType.System, username, "DtsPollingInterval",
                        oldConfig.DtsPollingInterval, config.DtsPollingInterval);
                }

                if (oldConfig.AlarmEvaluationInterval != config.AlarmEvaluationInterval)
                {
                    await _auditTrailService.LogConfigurationChangeAsync(
                        ConfigurationType.System, username, "AlarmEvaluationInterval",
                        oldConfig.AlarmEvaluationInterval, config.AlarmEvaluationInterval);
                }

                if (oldConfig.AlarmStatusBroadcastInterval != config.AlarmStatusBroadcastInterval)
                {
                    await _auditTrailService.LogConfigurationChangeAsync(
                        ConfigurationType.System, username, "AlarmStatusBroadcastInterval",
                        oldConfig.AlarmStatusBroadcastInterval, config.AlarmStatusBroadcastInterval);
                }

                if (oldConfig.HealthStatusBroadcastInterval != config.HealthStatusBroadcastInterval)
                {
                    await _auditTrailService.LogConfigurationChangeAsync(
                        ConfigurationType.System, username, "HealthStatusBroadcastInterval",
                        oldConfig.HealthStatusBroadcastInterval, config.HealthStatusBroadcastInterval);
                }

                if (oldConfig.AutoAlarmClearTimeout != config.AutoAlarmClearTimeout)
                {
                    await _auditTrailService.LogConfigurationChangeAsync(
                        ConfigurationType.System, username, "AutoAlarmClearTimeout",
                        oldConfig.AutoAlarmClearTimeout, config.AutoAlarmClearTimeout);
                }

                if (oldConfig.RateOfRiseWindowSeconds != config.RateOfRiseWindowSeconds)
                {
                    await _auditTrailService.LogConfigurationChangeAsync(
                        ConfigurationType.System, username, "RateOfRiseWindowSeconds",
                        oldConfig.RateOfRiseWindowSeconds, config.RateOfRiseWindowSeconds);
                }

                if (oldConfig.HealthMonitoringIntervalSeconds != config.HealthMonitoringIntervalSeconds)
                {
                    await _auditTrailService.LogConfigurationChangeAsync(
                        ConfigurationType.System, username, "HealthMonitoringIntervalSeconds",
                        oldConfig.HealthMonitoringIntervalSeconds, config.HealthMonitoringIntervalSeconds);
                }

                if (Math.Abs(oldConfig.DtsPointResolutionMeters - config.DtsPointResolutionMeters) > 0.001)
                {
                    await _auditTrailService.LogConfigurationChangeAsync(
                        ConfigurationType.System, username, "DtsPointResolutionMeters",
                        oldConfig.DtsPointResolutionMeters, config.DtsPointResolutionMeters);
                }

                // Health Threshold audit logging
                if (Math.Abs(oldConfig.CpuLoadThreshold - config.CpuLoadThreshold) > 0.01)
                {
                    await _auditTrailService.LogConfigurationChangeAsync(
                        ConfigurationType.System, username, "CpuLoadThreshold",
                        oldConfig.CpuLoadThreshold, config.CpuLoadThreshold);
                }

                if (Math.Abs(oldConfig.MemoryLoadThreshold - config.MemoryLoadThreshold) > 0.01)
                {
                    await _auditTrailService.LogConfigurationChangeAsync(
                        ConfigurationType.System, username, "MemoryLoadThreshold",
                        oldConfig.MemoryLoadThreshold, config.MemoryLoadThreshold);
                }

                if (Math.Abs(oldConfig.DtsModuleTempHighThreshold - config.DtsModuleTempHighThreshold) > 0.01)
                {
                    await _auditTrailService.LogConfigurationChangeAsync(
                        ConfigurationType.System, username, "DtsModuleTempHighThreshold",
                        oldConfig.DtsModuleTempHighThreshold, config.DtsModuleTempHighThreshold);
                }

                if (Math.Abs(oldConfig.DtsModuleTempLowThreshold - config.DtsModuleTempLowThreshold) > 0.01)
                {
                    await _auditTrailService.LogConfigurationChangeAsync(
                        ConfigurationType.System, username, "DtsModuleTempLowThreshold",
                        oldConfig.DtsModuleTempLowThreshold, config.DtsModuleTempLowThreshold);
                }

                if (oldConfig.DtsCommunicationTimeoutSeconds != config.DtsCommunicationTimeoutSeconds)
                {
                    await _auditTrailService.LogConfigurationChangeAsync(
                        ConfigurationType.System, username, "DtsCommunicationTimeoutSeconds",
                        oldConfig.DtsCommunicationTimeoutSeconds, config.DtsCommunicationTimeoutSeconds);
                }

                // Logging & Retention audit logging
                if (oldConfig.LogRetentionEnabled != config.LogRetentionEnabled)
                {
                    await _auditTrailService.LogConfigurationChangeAsync(
                        ConfigurationType.System, username, "LogRetentionEnabled",
                        oldConfig.LogRetentionEnabled, config.LogRetentionEnabled);
                }

                if (oldConfig.LogMaxAgeDays != config.LogMaxAgeDays)
                {
                    await _auditTrailService.LogConfigurationChangeAsync(
                        ConfigurationType.System, username, "LogMaxAgeDays",
                        oldConfig.LogMaxAgeDays, config.LogMaxAgeDays);
                }

                if (oldConfig.LogTotalQuotaMB != config.LogTotalQuotaMB)
                {
                    await _auditTrailService.LogConfigurationChangeAsync(
                        ConfigurationType.System, username, "LogTotalQuotaMB",
                        oldConfig.LogTotalQuotaMB, config.LogTotalQuotaMB);
                }

                if (oldConfig.LogHighWatermarkMB != config.LogHighWatermarkMB)
                {
                    await _auditTrailService.LogConfigurationChangeAsync(
                        ConfigurationType.System, username, "LogHighWatermarkMB",
                        oldConfig.LogHighWatermarkMB, config.LogHighWatermarkMB);
                }

                if (oldConfig.LogLowWatermarkMB != config.LogLowWatermarkMB)
                {
                    await _auditTrailService.LogConfigurationChangeAsync(
                        ConfigurationType.System, username, "LogLowWatermarkMB",
                        oldConfig.LogLowWatermarkMB, config.LogLowWatermarkMB);
                }

                if (oldConfig.LogCheckIntervalHours != config.LogCheckIntervalHours)
                {
                    await _auditTrailService.LogConfigurationChangeAsync(
                        ConfigurationType.System, username, "LogCheckIntervalHours",
                        oldConfig.LogCheckIntervalHours, config.LogCheckIntervalHours);
                }

                if (!string.Equals(oldConfig.MinimumLogLevel, config.MinimumLogLevel, StringComparison.OrdinalIgnoreCase))
                {
                    await _auditTrailService.LogConfigurationChangeAsync(
                        ConfigurationType.System, username, "MinimumLogLevel",
                        oldConfig.MinimumLogLevel, config.MinimumLogLevel);
                }
            }

            // Dynamically update active log level switch in Serilog
            LoggingConfiguration.SetMinimumLevel(config.MinimumLogLevel);

            // Publish configuration changed event
            await _eventAggregator.PublishAsync(new ConfigurationChangedEvent(
                ConfigurationType.System,
                changedBy,
                "FRMC",
                new Dictionary<string, object>
                {
                    { "NumberOfRelays", config.NumberOfRelays },
                    { "MaxZonesPerChannel", config.MaxZonesPerChannel },
                    { "DtsPollingInterval", config.DtsPollingInterval },
                    { "AlarmEvaluationInterval", config.AlarmEvaluationInterval },
                    { "AlarmStatusBroadcastInterval", config.AlarmStatusBroadcastInterval },
                    { "HealthStatusBroadcastInterval", config.HealthStatusBroadcastInterval },
                    { "AutoAlarmClearTimeout", config.AutoAlarmClearTimeout },
                    { "RateOfRiseWindowSeconds", config.RateOfRiseWindowSeconds },
                    { "HealthMonitoringIntervalSeconds", config.HealthMonitoringIntervalSeconds },
                    { "DtsPointResolutionMeters", config.DtsPointResolutionMeters },
                    { "CpuLoadThreshold", config.CpuLoadThreshold },
                    { "MemoryLoadThreshold", config.MemoryLoadThreshold },
                    { "DtsModuleTempHighThreshold", config.DtsModuleTempHighThreshold },
                    { "DtsModuleTempLowThreshold", config.DtsModuleTempLowThreshold },
                    { "DtsCommunicationTimeoutSeconds", config.DtsCommunicationTimeoutSeconds },
                    { "LogRetentionEnabled", config.LogRetentionEnabled },
                    { "LogMaxAgeDays", config.LogMaxAgeDays },
                    { "LogTotalQuotaMB", config.LogTotalQuotaMB },
                    { "LogHighWatermarkMB", config.LogHighWatermarkMB },
                    { "LogLowWatermarkMB", config.LogLowWatermarkMB },
                    { "LogCheckIntervalHours", config.LogCheckIntervalHours },
                    { "MinimumLogLevel", config.MinimumLogLevel }
                }
            ));

            return true;
        }


        //public async Task LoadConfigurationsAsync()
        //{
        //    try
        //    {
        //        var fullPath = Path.Combine(ConfigFolderPath, ConfigFileName);
        //        var absolutePath = Path.GetFullPath(fullPath);
        //        _logger.LogInformation("Attempting to load configuration from: {Path}", absolutePath);

        //        if (await _fileSystemPersistence.FileExistsAsync(fullPath))
        //        {
        //            _logger.LogInformation("Configuration file found at: {Path}", absolutePath);
        //            var json = await _fileSystemPersistence.ReadFileAsync(fullPath);

        //            // Deserialize the configuration-only format
        //            using var doc = JsonDocument.Parse(json);
        //            var root = doc.RootElement;

        //            _channels.Clear();

        //            foreach (var channelElement in root.EnumerateArray())
        //            {
        //                var configElement = channelElement.GetProperty("configuration");
        //                var channelConfig = JsonSerializer.Deserialize<ChannelConfiguration>(
        //                    configElement.GetRawText(),
        //                    JsonSerializerSettings.DefaultOptions);

        //                if (channelConfig != null)
        //                {
        //                    // Validate channel ID - must be >= 1
        //                    if (channelConfig.ChannelId < 1)
        //                    {
        //                        _logger.LogWarning("Skipping invalid channel with ID {ChannelId} - Channel IDs must be 1 or greater", channelConfig.ChannelId);
        //                        continue; // Skip this channel
        //                    }

        //                    var channel = new Channel(channelConfig);

        //                    // Load zone configurations
        //                    if (channelElement.TryGetProperty("zones", out var zonesElement))
        //                    {
        //                        channel.Zones = new List<Zone>();

        //                        foreach (var zoneElement in zonesElement.EnumerateArray())
        //                        {
        //                            var zoneConfigElement = zoneElement.GetProperty("configuration");
        //                            var zoneConfig = JsonSerializer.Deserialize<ZoneConfiguration>(
        //                                zoneConfigElement.GetRawText(),
        //                                JsonSerializerSettings.DefaultOptions);

        //                            if (zoneConfig != null)
        //                            {
        //                                // Initialize zone with configuration values
        //                                var zone = new Zone
        //                                {
        //                                    Configuration = zoneConfig,
        //                                    State = new ZoneState(
        //                                        zoneConfig.ZoneId,
        //                                        zoneConfig.Name,
        //                                        zoneConfig.StartPoint,
        //                                        zoneConfig.EndPoint,
        //                                        0,  // AverageTemperature - starts at 0 until DTS data arrives
        //                                        0,  // MaxTemperature
        //                                        0,  // MinTemperature
        //                                        false,  // PreAlarm
        //                                        false,  // MaxAlarm
        //                                        false,  // MinAlarm
        //                                        DateTime.UtcNow)
        //                                    {
        //                                        Enabled = zoneConfig.Enabled
        //                                    }
        //                                };
        //                                channel.Zones.Add(zone);
        //                            }
        //                        }
        //                    }

        //                    _channels.TryAdd(channelConfig.ChannelId, channel);
        //                }
        //            }

        //            _logger.LogInformation("Configuration loaded successfully from file: {Count} channels loaded (runtime state initialized with defaults)", _channels.Count);
        //            return;
        //        }
        //        else
        //        {
        //            _logger.LogWarning("Configuration file not found at: {Path}, loading defaults", absolutePath);
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "Error loading configuration.");
        //    }


        //    EnsureDefaultChannels();
        //    // Load system configuration
        //    await LoadSystemConfigurationAsync();
        //}
        public async Task LoadConfigurationsAsync()
        {
            try
            {
                var fullPath = Path.Combine(ConfigFolderPath, ConfigFileName);
                var absolutePath = Path.GetFullPath(fullPath);
                _logger.LogInformation("Attempting to load configuration from: {Path}", absolutePath);

                if (await _fileSystemPersistence.FileExistsAsync(fullPath))
                {
                    _logger.LogInformation("Configuration file found at: {Path}", absolutePath);
                    var json = await _fileSystemPersistence.ReadFileAsync(fullPath);

                    // Deserialize the configuration-only format
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    _channels.Clear();

                    foreach (var channelElement in root.EnumerateArray())
                    {
                        var configElement = channelElement.GetProperty("configuration");
                        var channelConfig = JsonSerializer.Deserialize<ChannelConfiguration>(
                            configElement.GetRawText(),
                            JsonSerializerSettings.DefaultOptions);

                        if (channelConfig != null)
                        {
                            // Validate channel ID - must be >= 1
                            if (channelConfig.ChannelId < 1)
                            {
                                _logger.LogWarning("Skipping invalid channel with ID {ChannelId} - Channel IDs must be 1 or greater", channelConfig.ChannelId);
                                continue; // Skip this channel
                            }

                            var channel = new Channel(channelConfig);

                            // Load zone configurations
                            if (channelElement.TryGetProperty("zones", out var zonesElement))
                            {
                                channel.Zones = new List<Zone>();

                                foreach (var zoneElement in zonesElement.EnumerateArray())
                                {
                                    var zoneConfigElement = zoneElement.GetProperty("configuration");
                                    var zoneConfig = JsonSerializer.Deserialize<ZoneConfiguration>(
                                        zoneConfigElement.GetRawText(),
                                        JsonSerializerSettings.DefaultOptions);

                                    if (zoneConfig != null)
                                    {
                                        // Initialize zone with configuration values
                                        var zone = new Zone
                                        {
                                            Configuration = zoneConfig,
                                            State = new ZoneState(
                                                zoneConfig.ZoneId,
                                                zoneConfig.Name,
                                                zoneConfig.StartPoint,
                                                zoneConfig.EndPoint,
                                                0,  // AverageTemperature - starts at 0 until DTS data arrives
                                                0,  // MaxTemperature
                                                0,  // MinTemperature
                                                false,  // PreAlarm
                                                false,  // MaxAlarm
                                                false,  // MinAlarm
                                                DateTime.UtcNow)
                                            {
                                                Enabled = zoneConfig.Enabled
                                            }
                                        };
                                        channel.Zones.Add(zone);
                                    }
                                }
                            }

                            _channels.TryAdd(channelConfig.ChannelId, channel);
                        }
                    }

                    _logger.LogInformation("Configuration loaded successfully from file: {Count} channels loaded (runtime state initialized with defaults)", _channels.Count);
                    return;
                }
                else
                {
                    _logger.LogWarning("Configuration file not found at: {Path}, loading defaults", absolutePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading configuration.");
            }


            EnsureDefaultChannels();
            // Load system configuration
            await LoadSystemConfigurationAsync();
        }

        private void EnsureDefaultChannels()
        {
            const int MaxChannels = 8;

            for (int channelId = 1; channelId <= MaxChannels; channelId++)
            {
                if (_channels.ContainsKey(channelId))
                    continue;

                var defaultConfig = new ChannelConfiguration
                {
                    ChannelId = channelId,
                    Name = $"Channel {channelId}",
                    ChannelLength = 0,
                    IsEnabled = false,
                    ScanPeriod = 0,
                    NumberOfZones = 0
                };

                var channel = new Channel(defaultConfig)
                {
                    Zones = new List<Zone>() // no zones by default
                };

                _channels.TryAdd(channelId, channel);
            }

            _logger.LogInformation(
                "EnsureDefaultChannels completed. Total channels in memory: {Count}",
                _channels.Count);
        }

        private void LoadDefaultChannelConfigurations()
        {
            _channels.Clear();

            _logger.LogInformation("Loading default channel configurations from IOptions. Available configs: {Count}", _defaultChannelConfigurations.Value?.Count ?? 0);
            foreach (var cfg in _defaultChannelConfigurations.Value)
            {
                _logger.LogInformation("Adding default channel: ChannelId={ChannelId}, Name={Name}, Length={Length}",
                    cfg.ChannelId, cfg.Name, cfg.ChannelLength);

                var channel = new Channel(cfg);

                // IMPORTANT: Zones are NOT created automatically
                // Zones will be created by DTSCM via SetZoneConfig messages
                //
                // VALID CONFIGURATIONS:
                // 1. Channel with NO zones (zones: [])
                // 2. Channel with zones covering full length (e.g., 0-10000m)
                // 3. Channel with SPARSE zones (e.g., Zone1: 0-3000m, Zone2: 5000-7000m, unassigned: 3000-5000m, 7000-10000m)
                //
                // All configurations are valid - DTSCM is the configuration master

                _channels.TryAdd(cfg.ChannelId, channel);
            }
            _logger.LogInformation("Default configurations loaded: {Count} channels (zones will be configured via DTSCM)", _channels.Count);
        }

        /// <summary>
        /// FIXED: Uses atomic file write (temp file + move) to prevent corruption.
        /// Re-throws exceptions so caller can handle them (for rollback).
        /// </summary>
        public async Task SaveConfigurationsAsync()
        {
            var fullPath = Path.Combine(ConfigFolderPath, ConfigFileName);
            var absolutePath = Path.GetFullPath(fullPath);
            var tempPath = fullPath + ".tmp";

            try
            {
                // Ensure config directory exists
                var configDir = Path.GetDirectoryName(absolutePath);
                if (!string.IsNullOrEmpty(configDir) && !Directory.Exists(configDir))
                {
                    Directory.CreateDirectory(configDir);
                }

                // Only save configuration data, NOT runtime state
                var configData = _channels.Values.Select(ch => new
                {
                    Configuration = ch.Configuration,
                    Zones = ch.Zones.Select(z => new
                    {
                        Configuration = z.Configuration
                        // State is intentionally excluded - saved separately in EventHistory on alarm events
                    }).ToList()
                }).ToList();

                var json = JsonSerializer.Serialize(configData, JsonSerializerSettings.DefaultOptions);

                // Write to temp file first (atomic operation)
                await _fileSystemPersistence.WriteFileAsync(tempPath, json);

                // Move temp file to final location (atomic on same filesystem)
                if (File.Exists(absolutePath))
                {
                    File.Delete(absolutePath); // Delete old file first on Windows
                }
                File.Move(tempPath, absolutePath);

                _logger.LogInformation("Configuration saved to: {Path} ({Count} channels, state excluded)",
                    absolutePath, _channels.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving configuration to {Path}", absolutePath);

                // Clean up temp file if it exists
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(cleanupEx, "Failed to clean up temp file {TempPath}", tempPath);
                }

                throw; // Re-throw for caller to handle (enables rollback)
            }
        }

        private async Task LoadSystemConfigurationAsync()
        {
            try
            {
                var fullPath = Path.Combine(ConfigFolderPath, SystemConfigFileName);
                var absolutePath = Path.GetFullPath(fullPath);

                if (await _fileSystemPersistence.FileExistsAsync(fullPath))
                {
                    _logger.LogInformation("Loading system configuration from: {Path}", absolutePath);
                    var json = await _fileSystemPersistence.ReadFileAsync(fullPath);
                    _systemConfiguration = JsonSerializer.Deserialize<SystemConfiguration>(
                        json,
                        JsonSerializerSettings.DefaultOptions);

                    if (_systemConfiguration != null && !string.IsNullOrWhiteSpace(_systemConfiguration.MinimumLogLevel))
                    {
                        LoggingConfiguration.SetMinimumLevel(_systemConfiguration.MinimumLogLevel);
                    }

                    // Load log retention settings directly from appsettings.json
                    LoadLogSettingsFromAppSettings(_systemConfiguration);

                    _logger.LogInformation("System configuration loaded successfully");
                }
                else
                {
                    _logger.LogWarning("System configuration file not found at: {Path}, using defaults", absolutePath);

                    // Create default system configuration
                    _systemConfiguration = new SystemConfiguration(
                        ipAddress: "CurrentIP",
                        subnetMask: "255.255.255.0",
                        gateway: "192.168.1.1",
                        systemTime: DateTime.Now,
                        softwareVersion: "0.1",
                        backgroundServiceInterval: 5000
                    );

                    // Load log retention settings from appsettings.json
                    LoadLogSettingsFromAppSettings(_systemConfiguration);

                    // Save the default configuration
                    await SaveSystemConfigurationAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading system configuration, using defaults");

                // Fallback to default
                _systemConfiguration = new SystemConfiguration(
                    ipAddress: "192.168.1.100",
                    subnetMask: "255.255.255.0",
                    gateway: "192.168.1.1",
                    systemTime: DateTime.Now,
                    softwareVersion: "0.1",
                    backgroundServiceInterval: 5000
                );

                LoadLogSettingsFromAppSettings(_systemConfiguration);
            }
        }

        private async Task SaveSystemConfigurationAsync()
        {
            try
            {
                var fullPath = Path.Combine(ConfigFolderPath, SystemConfigFileName);
                var absolutePath = Path.GetFullPath(fullPath);

                // Serialize system configuration without log retention fields (stored exclusively in appsettings.json)
                var jObj = Newtonsoft.Json.Linq.JObject.FromObject(_systemConfiguration ?? new SystemConfiguration());
                jObj.Remove("LogRetentionEnabled");
                jObj.Remove("LogMaxAgeDays");
                jObj.Remove("LogTotalQuotaMB");
                jObj.Remove("LogHighWatermarkMB");
                jObj.Remove("LogLowWatermarkMB");
                jObj.Remove("LogCheckIntervalHours");
                jObj.Remove("MinimumLogLevel");
                jObj.Remove("logRetentionEnabled");
                jObj.Remove("logMaxAgeDays");
                jObj.Remove("logTotalQuotaMB");
                jObj.Remove("logHighWatermarkMB");
                jObj.Remove("logLowWatermarkMB");
                jObj.Remove("logCheckIntervalHours");
                jObj.Remove("minimumLogLevel");

                var json = jObj.ToString(Newtonsoft.Json.Formatting.Indented);
                await _fileSystemPersistence.WriteFileAsync(fullPath, json);

                _logger.LogInformation("System configuration saved to: {Path}", absolutePath);

                if (_systemConfiguration != null)
                {
                    SyncLogSettingsToAppSettings(_systemConfiguration);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving system configuration");
            }
        }

        /// <summary>
        /// Loads log retention configuration from appsettings.json into the in-memory SystemConfiguration model.
        /// </summary>
        private void LoadLogSettingsFromAppSettings(SystemConfiguration? config)
        {
            if (config == null) return;

            try
            {
                var candidatePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                candidatePaths.Add(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
                candidatePaths.Add(Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"));

                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null)
                {
                    var hostAppSettings = Path.Combine(dir.FullName, "FireRakshak.FRMC.Host", "appsettings.json");
                    if (File.Exists(hostAppSettings)) candidatePaths.Add(hostAppSettings);

                    var directAppSettings = Path.Combine(dir.FullName, "appsettings.json");
                    if (File.Exists(directAppSettings)) candidatePaths.Add(directAppSettings);

                    dir = dir.Parent;
                }

                foreach (var configPath in candidatePaths)
                {
                    if (File.Exists(configPath))
                    {
                        var json = File.ReadAllText(configPath);
                        var jObj = Newtonsoft.Json.Linq.JObject.Parse(json);

                        if (jObj["Logging"]?["LogLevel"]?["Default"] != null)
                        {
                            config.MinimumLogLevel = jObj["Logging"]!["LogLevel"]!["Default"]!.ToString();
                        }

                        if (jObj["LogRetention"] != null)
                        {
                            var ret = jObj["LogRetention"];
                            if (ret["Enabled"] != null) config.LogRetentionEnabled = (bool)ret["Enabled"]!;
                            if (ret["MaxAgeDays"] != null) config.LogMaxAgeDays = (int)ret["MaxAgeDays"]!;
                            if (ret["TotalQuotaMB"] != null) config.LogTotalQuotaMB = (long)ret["TotalQuotaMB"]!;
                            if (ret["HighWatermarkMB"] != null) config.LogHighWatermarkMB = (long)ret["HighWatermarkMB"]!;
                            if (ret["LowWatermarkMB"] != null) config.LogLowWatermarkMB = (long)ret["LowWatermarkMB"]!;
                            if (ret["CheckIntervalHours"] != null) config.LogCheckIntervalHours = (int)ret["CheckIntervalHours"]!;
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load log retention settings from appsettings.json");
            }
        }

        /// <summary>
        /// Synchronizes logging and retention settings configured from DTSCM into appsettings.json.
        /// </summary>
        private void SyncLogSettingsToAppSettings(SystemConfiguration config)
        {
            try
            {
                var candidatePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // 1. AppContext BaseDirectory (runtime bin folder)
                candidatePaths.Add(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));

                // 2. Current Working Directory
                candidatePaths.Add(Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"));

                // 3. Search up parent directory tree for project-level appsettings.json
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null)
                {
                    var hostAppSettings = Path.Combine(dir.FullName, "FireRakshak.FRMC.Host", "appsettings.json");
                    if (File.Exists(hostAppSettings))
                    {
                        candidatePaths.Add(hostAppSettings);
                    }

                    var directAppSettings = Path.Combine(dir.FullName, "appsettings.json");
                    if (File.Exists(directAppSettings))
                    {
                        candidatePaths.Add(directAppSettings);
                    }

                    dir = dir.Parent;
                }

                foreach (var configPath in candidatePaths)
                {
                    if (File.Exists(configPath))
                    {
                        var json = File.ReadAllText(configPath);
                        var jObj = Newtonsoft.Json.Linq.JObject.Parse(json);

                        if (jObj["Logging"] == null)
                            jObj["Logging"] = new Newtonsoft.Json.Linq.JObject();
                        if (jObj["Logging"]["LogLevel"] == null)
                            jObj["Logging"]["LogLevel"] = new Newtonsoft.Json.Linq.JObject();

                        if (!string.IsNullOrWhiteSpace(config.MinimumLogLevel))
                        {
                            jObj["Logging"]["LogLevel"]["Default"] = config.MinimumLogLevel;
                        }

                        if (jObj["LogRetention"] == null)
                            jObj["LogRetention"] = new Newtonsoft.Json.Linq.JObject();

                        jObj["LogRetention"]["Enabled"] = config.LogRetentionEnabled;
                        jObj["LogRetention"]["MaxAgeDays"] = config.LogMaxAgeDays;
                        jObj["LogRetention"]["TotalQuotaMB"] = config.LogTotalQuotaMB;
                        jObj["LogRetention"]["HighWatermarkMB"] = config.LogHighWatermarkMB;
                        jObj["LogRetention"]["LowWatermarkMB"] = config.LogLowWatermarkMB;
                        jObj["LogRetention"]["CheckIntervalHours"] = config.LogCheckIntervalHours;

                        File.WriteAllText(configPath, jObj.ToString(Newtonsoft.Json.Formatting.Indented));
                        _logger.LogInformation("Synchronized log configuration from DTSCM to {Path}", configPath);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to synchronize log configuration to appsettings.json");
            }
        }
        public async Task<DtsConfiguration> LoadDtsConfigurationAsync()
        {
            var folder = "Config";
            var file = "dts_configuration.json";
            var path = Path.Combine(folder, file);

            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            if (!File.Exists(path))
            {
                var defaultConfig = new DtsConfiguration
                {
                    PulseWidth = 100,
                    PumpCurrent = 75,
                    ChannelLengths = new List<byte> { 10, 20, 30, 40 }
                };

                var json = JsonSerializer.Serialize(defaultConfig, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                await File.WriteAllTextAsync(path, json);
                return defaultConfig;
            }

            var content = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<DtsConfiguration>(content)!;
        }

        public EthernetConfigFile GetEthernetConfig()
        {
            if (_ethernetConfig != null)
                return _ethernetConfig;

            if (!File.Exists(EthConfigPath))
            {
                _ethernetConfig = new EthernetConfigFile();
                SaveEthernetConfig();
                return _ethernetConfig;
            }

            var json = File.ReadAllText(EthConfigPath);
            _ethernetConfig =
                JsonSerializer.Deserialize<EthernetConfigFile>(json)
                ?? new EthernetConfigFile();

            _ethernetConfig.EthernetPorts ??= new List<EthernetConfiguration>();
            return _ethernetConfig;
        }

        public void SaveEthernetConfig()
        {
            if (_ethernetConfig == null)
                return;

            var json = JsonSerializer.Serialize(
                _ethernetConfig,
                new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(EthConfigPath, json);
        }

        public void UpdateEthernetPort(EthernetConfiguration port)
        {
            var config = GetEthernetConfig();

            // Match by Role (stable) instead of InterfaceName (can change between boots)
            var existing = config.EthernetPorts
                .FirstOrDefault(p => p.Role.Equals(port.Role, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                config.EthernetPorts.Add(port);
            }
            else
            {
                // Update interface name (may have changed after boot)
                existing.InterfaceName = port.InterfaceName;
                existing.Ip_Address = port.Ip_Address;
                existing.SubnetMask = port.SubnetMask;
                existing.Gateway = port.Gateway;
                existing.DhcpEnabled = port.DhcpEnabled;
                existing.LastUpdatedUtc = DateTime.UtcNow;
            }

            SaveEthernetConfig();
        }

        /// <summary>
        /// Gets the highest relay ID currently assigned to any zone.
        /// Used to validate relay count reduction requests.
        /// </summary>
        /// <returns>The highest assigned relay ID, or 0 if no relays are assigned.</returns>
        private int GetMaxAssignedRelayId()
        {
            int maxRelayId = 0;

            foreach (var channel in _channels.Values)
            {
                foreach (var zone in channel.Zones)
                {
                    if (zone.Configuration.AssignedRelayId.HasValue &&
                        zone.Configuration.AssignedRelayId.Value > maxRelayId)
                    {
                        maxRelayId = zone.Configuration.AssignedRelayId.Value;
                    }
                }
            }

            return maxRelayId;
        }

    }
}
