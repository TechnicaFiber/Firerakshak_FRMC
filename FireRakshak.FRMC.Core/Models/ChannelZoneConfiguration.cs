using System.Collections.Generic;

namespace FireRakshak.FRMC.Core.Models
{
    /// <summary>
    /// Represents all zones that belong to a single DTS channel.
    /// </summary>
    public class ChannelZoneConfiguration
    {
        /// <summary>
        /// Channel ID (0–7)
        /// </summary>
        public int ChannelId { get; set; }

        /// <summary>
        /// List of all zones configured under this channel.
        /// </summary>
        public List<Zone> Zones { get; set; } = new List<Zone>();

        public ChannelZoneConfiguration() { }

        public ChannelZoneConfiguration(int channelId, List<Zone> zones)
        {
            ChannelId = channelId;
            Zones = zones ?? new List<Zone>();
        }
    }
}
