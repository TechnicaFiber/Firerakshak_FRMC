using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FireRakshak.FRMC.Managers.AlarmManager;
using FireRakshak.FRMC.Managers.SystemHealthManager;
using FireRakshak.FRMC.Managers.SystemConfigurationManager; // for Channel config types
using FireRakshak.FRMC.Infrastructure.Services;
using FireRakshak.FRMC.Core.Models;
using FireRakshak.FRMC.Core.EventAggregator;
using FireRakshak.FRMC.Core.Events;
using Microsoft.Extensions.Logging;
using System.Net.NetworkInformation;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;

namespace FireRakshak.FRMC.Managers.ExternalInterfacesManager
{
    public class ExternalInterfacesManager : IExternalInterfacesManager
    {
        private readonly IAlarmManager _alarmManager;
        private readonly ISystemHealthManager _systemHealthManager;

        // FIX: Fully qualify to avoid conflict with Microsoft IConfigurationManager
        private readonly FireRakshak.FRMC.Managers.SystemConfigurationManager.IConfigurationManager _configurationManager;

        private readonly ILogger<ExternalInterfacesManager> _logger;
        private readonly ClockService _clockService;
        private readonly NetworkAdapterOptions _networkAdapterOptions;
        private readonly IConfiguration _configuration;
        private readonly IEventAggregator _eventAggregator;
        private readonly FireRakshak.FRMC.Managers.ServiceManager.NetworkInterfaceService _networkService;

        public ExternalInterfacesManager(
            IAlarmManager alarmManager,
            ISystemHealthManager systemHealthManager,
            FireRakshak.FRMC.Managers.SystemConfigurationManager.IConfigurationManager configurationManager,  // FIX HERE
            ClockService clockService,
            ILogger<ExternalInterfacesManager> logger,
            IOptions<NetworkAdapterOptions> networkAdapterOptions,
            IConfiguration configuration,
            IEventAggregator eventAggregator,
            FireRakshak.FRMC.Managers.ServiceManager.NetworkInterfaceService networkService)
        {
            _alarmManager = alarmManager;
            _systemHealthManager = systemHealthManager;
            _configurationManager = configurationManager;  // FIX HERE
            _clockService = clockService;
            _logger = logger;
            _networkAdapterOptions = networkAdapterOptions.Value;
            _configuration = configuration;
            _eventAggregator = eventAggregator;
            _networkService = networkService;
        }

        // ============================================================
        // EXTERNAL PARAMETERS
        // ============================================================
        public async Task<Dictionary<string, object>> GetExternalParametersAsync()
        {
            _logger.LogInformation("Retrieving external parameters.");

            var parameters = new Dictionary<string, object>();

            var activeAlarms = await _alarmManager.GetActiveAlarmsAsync();
            parameters["ActiveAlarmsCount"] = activeAlarms.Count;
            parameters["ActiveAlarms"] = activeAlarms;

            var health = await _systemHealthManager.GetCurrentHealthAsync();
            parameters["SystemHealth"] = health;

            var systemConfig = await _configurationManager.GetSystemConfigurationAsync();
            parameters["SystemTime"] = systemConfig.SystemTime;

            return parameters;
        }

        // ============================================================
        // ALARM RESET
        // ============================================================
        public async Task ProcessAlarmResetAsync(int alarmId)
        {
            _logger.LogInformation("Processing alarm reset for AlarmId: {AlarmId}", alarmId);

            // Publish alarm reset event instead of calling ClearAlarmAsync directly
            // This ensures consistent handling with Modbus and other sources
            await _eventAggregator.PublishAsync(
                new AlarmResetRequestedEvent("WebSocket", alarmId, DateTime.UtcNow)
            );
        }

        // ============================================================
        // SYSTEM IP CONFIG
        // ============================================================
        public async Task<SystemConfiguration> GetIpConfigurationAsync()
        {
            return await _configurationManager.GetSystemConfigurationAsync();
        }

        public async Task ProcessIpConfigurationAsync(
            string newIpAddress,
            string newSubnetMask,
            string newGateway,
            string? adapterName,
            string changedBy = "Admin")
        {
            _logger.LogInformation("Processing IP configuration by {User}.", changedBy);

            bool simulate = _configuration.GetValue<bool>("SystemSettings:SimulateConfigurationChanges:Ip");
            if (simulate)
            {
                _logger.LogInformation("[SIMULATION] IP change simulated.");
                return;
            }

            // Actual adapter lookup
            string? actualAdapterName = null;

            if (!string.IsNullOrEmpty(adapterName))
            {
                if (_networkAdapterOptions.AdapterMappings.TryGetValue(adapterName, out var mapped))
                    actualAdapterName = mapped;
                else
                    actualAdapterName = adapterName;
            }

            if (!string.IsNullOrEmpty(actualAdapterName))
            {
                _logger.LogInformation("Applying IP configuration to adapter: {AdapterName}", actualAdapterName);
                
                _networkService.ApplyStaticIp(
                    actualAdapterName,
                    newIpAddress,
                    newSubnetMask,
                    newGateway,
                    newGateway); // Use gateway as DNS fallback
            }
            else
            {
                var current = await _configurationManager.GetSystemConfigurationAsync();
                var updated = new SystemConfiguration(
                    newIpAddress, newSubnetMask, newGateway,
                    current.SystemTime,
                    current.SoftwareVersion,
                    current.BackgroundServiceInterval);

                await _configurationManager.UpdateSystemConfigurationAsync(updated, changedBy);
            }
        }

        // ============================================================
        // SYSTEM CLOCK
        // ============================================================
        public Task<DateTime> GetClockConfigurationAsync()
        {
            return _clockService.ReadSystemClock();
        }

        public async Task<bool> ProcessClockConfigurationAsync(DateTime newTime)
        {
            var (success, _) = await _clockService.UpdateSystemClock(newTime);
            return success;
        }

        public Task<(bool Success, string Message)> ProcessNetworkTimeSynchronizationAsync(bool disable)
        {
            return disable
                ? _clockService.DisableNetworkTimeSynchronization()
                : _clockService.EnableNetworkTimeSynchronization();
        }

        // ============================================================
        // CHANNEL CONFIGURATION (NEW)
        // ============================================================
        public async Task<bool> AddOrUpdateChannelConfigurationAsync(ChannelConfiguration config, string changedBy = "Admin")
        {
            await _configurationManager.AddOrUpdateChannelConfigurationAsync(config, changedBy);
            return true;  // MUST return a bool because method signature says Task<bool>
        }


        public async Task<ChannelConfiguration?> GetChannelConfigurationAsync(int channelId)
        {
            return await _configurationManager.GetChannelConfigurationAsync(channelId);
        }

        public async Task<List<ChannelConfiguration>> GetAllChannelConfigurationsAsync()
        {
            return await _configurationManager.GetAllChannelConfigurationsAsync();
        }
    }
}
