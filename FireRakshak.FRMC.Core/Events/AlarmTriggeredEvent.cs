using System;
using FireRakshak.FRMC.Core.Enums;

namespace FireRakshak.FRMC.Core.Events
{
    /// <summary>
    /// Represents an event that is triggered when an alarm condition is met within a specific zone.
    /// This event is typically published by the <see cref="Managers.ChannelsManager.ChannelsManager"/>
    /// when temperature thresholds are breached, and is consumed by the <see cref="Managers.AlarmManager.AlarmManager"/>.
    /// </summary>
    public class AlarmTriggeredEvent : IEvent
    {
        /// <summary>
        /// Gets or sets the unique identifier of the zone where the alarm was triggered.
        /// </summary>
        public int ChannelId { get; }
        public int ZoneId { get; set; }

        /// <summary>
        /// Gets or sets the type of alarm that was triggered (e.g., MaxTemperature, RateOfRise).
        /// </summary>
        public AlarmType Type { get; set; }

        /// <summary>
        /// Gets or sets the value that triggered the alarm (e.g., the temperature reading).
        /// </summary>
        public double Value { get; set; }

        /// <summary>
        /// Gets or sets the timestamp when the alarm was triggered (in UTC).
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the position (in meters) where the alarm was triggered.
        /// </summary>
        public double? Position { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="AlarmTriggeredEvent"/> class.
        /// </summary>
        /// <param name="zoneId">The unique identifier of the zone where the alarm was triggered.</param>
        /// <param name="type">The type of alarm that was triggered.</param>
        /// <param name="value">The value that triggered the alarm.</param>
        /// <param name="timestamp">The UTC timestamp when the alarm was triggered.</param>
        /// <param name="position">The position in meters where the alarm was triggered (optional).</param>
        public AlarmTriggeredEvent(int channelId, int zoneId, AlarmType type, double value, DateTime timestamp, double? position = null)
        {
            ChannelId = channelId;
            ZoneId = zoneId;
            Type = type;
            Value = value;
            Timestamp = timestamp;
            Position = position;
        }
    }
}
