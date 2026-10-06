using System;

namespace FireRakshak.FRMC.Core.Models
{
    /// <summary>
    /// Represents a zone within a DTS channel, encapsulating its configuration and current state.
    /// </summary>
    public class Zone
    {
        public ZoneConfiguration Configuration { get; set; }
        public ZoneState State { get; set; }

        public Zone(ZoneConfiguration configuration)
        {
            Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            State = new ZoneState(configuration.ZoneId,"Zone", 0, 0, 0, 0, 0, false, false, false, DateTime.UtcNow);
        }

        // Constructor for deserialization purposes (e.g., from file)
        public Zone() { }
    }
}
