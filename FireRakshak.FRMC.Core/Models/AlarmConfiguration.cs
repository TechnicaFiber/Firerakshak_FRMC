using System.Collections.Generic;
using FireRakshak.FRMC.Core.Enums;

namespace FireRakshak.FRMC.Core.Models
{
    /// <summary>
    /// Represents the configuration settings for a specific alarm within a zone.
    /// This includes the alarm type, associated physical relay, and the thresholds
    /// that define when the alarm should be triggered.
    /// </summary>
    public class AlarmConfiguration
    {
        /// <summary>
        /// Gets or sets the unique identifier for this alarm configuration.
        /// </summary>
        public int AlarmId { get; set; }

        /// <summary>
        /// Gets or sets the identifier of the zone to which this alarm configuration applies.
        /// </summary>
        public int ZoneId { get; set; }

        /// <summary>
        /// Gets or sets the type of alarm this configuration defines (e.g., MaxTemperature, RateOfRise).
        /// </summary>
        public AlarmType Type { get; set; }

        /// <summary>
        /// Gets or sets the number of the physical relay that should be activated when this alarm triggers.
        /// A value of 0 might indicate no physical relay is associated.
        /// </summary>
        public int RelayNumber { get; set; }

        /// <summary>
        /// Gets or sets a dictionary of thresholds for this alarm.
        /// Keys could be "MaxTemp", "RateOfRiseLimit", etc., and values are the corresponding limits.
        /// </summary>
        public Dictionary<string, double> Thresholds { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="AlarmConfiguration"/> class.
        /// </summary>
        /// <param name="alarmId">The unique identifier for the alarm configuration.</param>
        /// <param name="zoneId">The ID of the zone this configuration applies to.</param>
        /// <param name="type">The type of alarm.</param>
        /// <param name="relayNumber">The number of the physical relay associated with this alarm.</param>
        /// <param name="thresholds">A dictionary of thresholds for the alarm.</param>
        public AlarmConfiguration(int alarmId, int zoneId, AlarmType type, int relayNumber, Dictionary<string, double> thresholds)
        {
            AlarmId = alarmId;
            ZoneId = zoneId;
            Type = type;
            RelayNumber = relayNumber;
            Thresholds = thresholds;
        }
    }
}
