using System;
using FireRakshak.FRMC.Core.Enums;

namespace FireRakshak.FRMC.Core.Models
{
    /// <summary>
    /// Represents an alarm that is currently active within the system.
    /// This model captures all relevant information about a triggered alarm.
    /// </summary>
    public class ActiveAlarm
    {
        public int AlarmId { get; set; }

        /// <summary>
        /// Channel ID where the alarm occurred.
        /// </summary>
        public int ChannelId { get; set; }  

        public int ZoneId { get; set; }

        public AlarmType Type { get; set; }

        public double TriggerValue { get; set; }

        public DateTime TriggeredAt { get; set; }

        /// <summary>
        /// Position in meters where the alarm was triggered.
        /// </summary>
        public double? Position { get; set; }

        public ZoneState ZoneStateSnapshot { get; set; }

        public ActiveAlarm(int alarmId, int channelId, int zoneId, AlarmType type, double triggerValue,
                           DateTime triggeredAt, ZoneState zoneStateSnapshot, double? position = null)
        {
            AlarmId = alarmId;
            ChannelId = channelId;
            ZoneId = zoneId;
            Type = type;
            TriggerValue = triggerValue;
            TriggeredAt = triggeredAt;
            ZoneStateSnapshot = zoneStateSnapshot;
            Position = position;
        }
    }
}
