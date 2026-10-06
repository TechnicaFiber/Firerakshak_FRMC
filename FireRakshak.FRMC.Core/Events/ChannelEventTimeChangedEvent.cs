using System;

namespace FireRakshak.FRMC.Core.Events
{
    /// <summary>
    /// Represents an event that is triggered when a channel's last alarm time changes.
    /// This includes when an alarm is triggered (time is set) or when all alarms are cleared (time is set to null).
    /// Used by ModbusTcpServer to update channel event time registers.
    /// </summary>
    public class ChannelEventTimeChangedEvent : IEvent
    {
        /// <summary>
        /// Gets the channel ID for which the event time changed.
        /// </summary>
        public int ChannelId { get; }

        /// <summary>
        /// Gets the last alarm timestamp, or null if all alarms have been cleared.
        /// </summary>
        public DateTime? LastAlarmTime { get; }

        /// <summary>
        /// Gets the timestamp when this event was raised.
        /// </summary>
        public DateTime Timestamp { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="ChannelEventTimeChangedEvent"/> class.
        /// </summary>
        /// <param name="channelId">The channel ID</param>
        /// <param name="lastAlarmTime">The last alarm timestamp, or null if cleared</param>
        /// <param name="timestamp">The UTC timestamp when this event occurred</param>
        public ChannelEventTimeChangedEvent(int channelId, DateTime? lastAlarmTime, DateTime timestamp)
        {
            ChannelId = channelId;
            LastAlarmTime = lastAlarmTime;
            Timestamp = timestamp;
        }
    }
}
