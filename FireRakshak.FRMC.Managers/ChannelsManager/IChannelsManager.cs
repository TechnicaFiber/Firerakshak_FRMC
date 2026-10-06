using System.Collections.Generic;
using System.Threading.Tasks;
using FireRakshak.FRMC.Core.Models;

namespace FireRakshak.FRMC.Managers.ChannelsManager
{
    public interface IChannelsManager
    {
        Task<ZoneConfiguration> GetZoneConfigurationAsync(int zoneId);
        Task UpdateZoneConfigurationAsync(ZoneConfiguration config);
        Task<ZoneState> GetZoneStateAsync(int zoneId);
        Task<ChannelState> GetChannelStateAsync(int channelId);

        Task InitializeChannelsAsync();

        public void GetZoneState(int channelNumber, int zoneIndex);

        public void GetNumberOfZones(out int totalZones, out int enabledZones);

        /// <summary>
        /// Gets all configured channels with their zones.
        /// Used by ModbusTcpServer for register initialization and mapping.
        /// </summary>
        List<Channel> GetChannels();
    }
}
