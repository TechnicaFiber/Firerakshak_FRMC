using System;
using System.Collections.Generic;

namespace FireRakshak.FRMC.Core.Models
{
    /// <summary>
    /// Represents a DTS channel, encapsulating its configuration, current state, and associated zones.
    /// </summary>
    public class Channel
    {
        public ChannelConfiguration Configuration { get; set; }
        public ChannelState State { get; set; }
        public List<Zone> Zones { get; set; } = new List<Zone>();

        public Channel(ChannelConfiguration configuration)
        {
            Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            State = new ChannelState(configuration.ChannelId, configuration.Length, configuration.CorrectionLength, Array.Empty<TemperatureDataPoint>(), false, DateTime.UtcNow);
        }

        // Constructor for deserialization purposes (e.g., from file)
        public Channel() { }
    }
}
