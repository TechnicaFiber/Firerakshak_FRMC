using System;
using FireRakshak.FRMC.Core.Enums;

namespace FireRakshak.FRMC.Core.Events
{
    /// <summary>
    /// Represents an event triggered when an alarm condition automatically clears
    /// because the measured values returned to normal (below/above thresholds).
    /// This is different from manual alarm acknowledgment/reset.
    /// </summary>
    public class AlarmAutoClearedEvent : IEvent
    {
        /// <summary>
        /// Gets the channel ID where the alarm was cleared.
        /// </summary>
        public int ChannelId { get; }

        /// <summary>
        /// Gets the zone ID where the alarm was cleared.
        /// </summary>
        public int ZoneId { get; }

        /// <summary>
        /// Gets the type of alarm that was cleared.
        /// </summary>
        public AlarmType Type { get; }

        /// <summary>
        /// Gets the timestamp when the alarm condition cleared (in UTC).
        /// </summary>
        public DateTime Timestamp { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="AlarmAutoClearedEvent"/> class.
        /// </summary>
        /// <param name="channelId">The channel ID where the alarm was cleared.</param>
        /// <param name="zoneId">The zone ID where the alarm was cleared.</param>
        /// <param name="type">The type of alarm that was cleared.</param>
        /// <param name="timestamp">The UTC timestamp when the alarm cleared.</param>
        public AlarmAutoClearedEvent(int channelId, int zoneId, AlarmType type, DateTime timestamp)
        {
            ChannelId = channelId;
            ZoneId = zoneId;
            Type = type;
            Timestamp = timestamp;
        }
    }
}
