namespace FireRakshak.FRMC.Core.Models
{
    /// <summary>
    /// Represents the configuration settings for a single Zone in a Channel.
    /// This model is used internally by FRMC and is mapped to/from DTSCM ZoneInfo.
    /// </summary>
    public class ZoneConfiguration
    {
        /// <summary>
        /// Channel ID this Zone belongs to (0�7)
        /// </summary>
        public int ChannelId { get; set; }

        /// <summary>
        /// Zone number inside the channel (1, 2, 3�)
        /// </summary>
        public int ZoneId { get; set; }

        /// <summary>
        /// Human readable name (e.g., "Zone 3")
        /// </summary>
        public string Name { get; set; } = string.Empty;

        public double StartPoint { get; set; }
        public double EndPoint { get; set; }
        public double MaxTemp { get; set; }
        public double MinTemp { get; set; }

        /// <summary>
        /// DTSCM "PreAlarm" comes from MinTemp + something, we store separately.
        /// </summary>
        public double PreAlarm { get; set; }

        /// <summary>
        /// Rate of Rise threshold in °C/minute.
        /// Typical fire detection thresholds: 5-15 °C/minute.
        /// Alarm triggers when temperature rise rate exceeds this value.
        /// </summary>
        public double RoRThreshold { get; set; }

        /// <summary>
        /// Deviation threshold in °C (difference between max and min temperature in zone).
        /// Alarm triggers when (MaxTemp - MinTemp) exceeds this value.
        /// </summary>
        public double DeviationThreshold { get; set; }
        public bool Enabled { get; set; }
        public bool IsMaxTempEnabled { get; set; }
        public bool IsMinTempEnabled { get; set; }
        public bool IsPreAlarmEnabled { get; set; }
        public bool IsRateOfRiseEnabled { get; set; }
        public bool IsDeviationEnabled { get; set; }

        /// <summary>
        /// Relay assigned to this zone. When ANY enabled alarm triggers for this zone,
        /// the assigned relay will be activated automatically.
        /// </summary>
        public int? AssignedRelayId { get; set; }

        public ZoneConfiguration() { }
    }
}
