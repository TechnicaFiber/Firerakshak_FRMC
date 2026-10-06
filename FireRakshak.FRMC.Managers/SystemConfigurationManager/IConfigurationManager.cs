using System.Collections.Generic;
using System.Threading.Tasks;
using FireRakshak.FRMC.Core.Models;

namespace FireRakshak.FRMC.Managers.SystemConfigurationManager
{
    public interface IConfigurationManager
    {
        // ---------------------------
        // CHANNEL MANAGEMENT
        // ---------------------------

        List<Channel> GetChannels();
        Task<bool> ReplaceZoneConfigurationAsync(int channelId, List<ZoneConfiguration> newZones, string changedBy = "Admin");

        Task<bool> UpdateChannelConfigurationAsync(ChannelConfiguration channelConfig, string changedBy = "Admin");

        Task<ChannelConfiguration?> GetChannelConfigurationAsync(int channelId);

        Task AddOrUpdateChannelConfigurationAsync(ChannelConfiguration config, string changedBy = "Admin");

        Task<List<ChannelConfiguration>> GetAllChannelConfigurationsAsync();

        Task UpdateChannelStateAsync(int channelId, ChannelState state);

        Task<DtsConfiguration> LoadDtsConfigurationAsync();
        // ---------------------------
        // ZONE MANAGEMENT
        // ---------------------------

        /// <summary>
        /// Add or update a single zone in a specific channel
        /// </summary>
        Task<bool> UpdateSingleZoneAsync(int channelId, int zoneId, ZoneConfiguration zoneConfig, string changedBy = "Admin");

        /// <summary>
        /// Remove a zone by channel & zone ID
        /// </summary>
        Task<bool> RemoveZoneConfigurationAsync(int channelId, int zoneId, string changedBy = "Admin");


        /// <summary>
        /// Get all zone configurations for all channels (needed for DTSCM)
        /// </summary>
        List<ZoneConfiguration> GetZoneConfigurations();

        /// <summary>
        /// Get a specific zone configuration by channel and zone ID
        /// </summary>
        Task<ZoneConfiguration?> GetZoneConfigAsync(int channelId, int zoneId);

        /// <summary>
        /// Add or update ENTIRE zone configuration list (DTSCM batch update)
        /// </summary>
        //Task AddOrUpdateZoneConfigurationAsync(List<ZoneConfiguration> zones);

        Task UpdateZoneStateAsync(int channelId, int zoneId, ZoneState state);

        // ---------------------------
        // SYSTEM CONFIGURATION (REQUIRED)
        // ---------------------------

        Task<SystemConfiguration> GetSystemConfigurationAsync();

        Task<bool> UpdateSystemConfigurationAsync(SystemConfiguration config, string changedBy = "Admin");

        EthernetConfigFile GetEthernetConfig();

        void SaveEthernetConfig();

        void UpdateEthernetPort(EthernetConfiguration port);
        // ---------------------------
        // PERSISTENCE
        // ---------------------------

        Task LoadConfigurationsAsync();

        Task SaveConfigurationsAsync();
    }
}
