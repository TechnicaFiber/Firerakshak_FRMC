using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FireRakshak.FRMC.Core.Models;

namespace FireRakshak.FRMC.Managers.ExternalInterfacesManager
{
    /// <summary>
    /// Defines the contract for managing external interfaces and aggregating data for external consumption.
    /// </summary>
    public interface IExternalInterfacesManager
    {
        // ---------------------------------------------------------
        // EXTERNAL PARAMETERS
        // ---------------------------------------------------------
        Task<Dictionary<string, object>> GetExternalParametersAsync();

        // ---------------------------------------------------------
        // ALARM MANAGEMENT
        // ---------------------------------------------------------
        Task ProcessAlarmResetAsync(int alarmId);

        // ---------------------------------------------------------
        // IP CONFIGURATION
        // ---------------------------------------------------------
        Task<SystemConfiguration> GetIpConfigurationAsync();
        Task ProcessIpConfigurationAsync(string newIpAddress, string newSubnetMask, string newGateway, string? adapterName, string changedBy = "Admin");

        // ---------------------------------------------------------
        // CLOCK CONFIGURATION
        // ---------------------------------------------------------
        Task<DateTime> GetClockConfigurationAsync();
        Task<bool> ProcessClockConfigurationAsync(DateTime newTime);

        // ---------------------------------------------------------
        // NTP / TIME SYNC
        // ---------------------------------------------------------
        Task<(bool Success, string Message)> ProcessNetworkTimeSynchronizationAsync(bool disable);

        // ---------------------------------------------------------
        // CHANNEL CONFIGURATION (NEW – REQUIRED FOR DTSCM)
        // ---------------------------------------------------------

        /// <summary>
        /// Returns all channel configurations.
        /// </summary>
        Task<List<ChannelConfiguration>> GetAllChannelConfigurationsAsync();

        /// <summary>
        /// Gets a specific channel's configuration.
        /// </summary>
        Task<ChannelConfiguration?> GetChannelConfigurationAsync(int channelId);

        /// <summary>
        /// Adds or updates a specific channel configuration.
        /// </summary>
        Task<bool> AddOrUpdateChannelConfigurationAsync(ChannelConfiguration config, string changedBy = "Admin");
    }
}
