using System;

namespace FireRakshak.FRMC.Core.Events
{
    /// <summary>
    /// Represents an event that is triggered when an alarm condition is cleared in the system.
    /// This event is typically published by the <see cref="Managers.AlarmManager.AlarmManager"/>
    /// to notify other components that a previously active alarm is no longer present.
    /// </summary>
    public class AlarmClearedEvent : IEvent
    {
        /// <summary>
        /// Gets or sets the unique identifier of the alarm that was cleared.
        /// </summary>
        public int AlarmId { get; set; }

        /// <summary>
        /// Gets or sets the timestamp when the alarm was cleared (in UTC).
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="AlarmClearedEvent"/> class.
        /// </summary>
        /// <param name="alarmId">The unique identifier of the alarm that was cleared.</param>
        /// <param name="timestamp">The UTC timestamp when the alarm was cleared.</param>
        public AlarmClearedEvent(int alarmId, DateTime timestamp)
        {
            AlarmId = alarmId;
            Timestamp = timestamp;
        }
    }
}
