using System;

namespace FireRakshak.FRMC.Core.Events
{
    /// <summary>
    /// Represents an event triggered when an alarm reset is requested.
    /// This can be from manual operator action (WebSocket) or external Modbus master.
    /// </summary>
    public class AlarmResetRequestedEvent : IEvent
    {
        /// <summary>
        /// Gets the source of the reset request (e.g., "Modbus", "WebSocket", "Manual").
        /// </summary>
        public string Source { get; }

        /// <summary>
        /// Gets the timestamp when the reset was requested (in UTC).
        /// </summary>
        public DateTime Timestamp { get; }

        /// <summary>
        /// Gets whether to reset all alarms (true) or a specific alarm (false).
        /// </summary>
        public bool ResetAll { get; }

        /// <summary>
        /// Gets the specific alarm ID to reset (only if ResetAll is false).
        /// </summary>
        public int? AlarmId { get; }

        /// <summary>
        /// Initializes a new instance for resetting all alarms.
        /// </summary>
        /// <param name="source">The source of the reset request.</param>
        /// <param name="timestamp">The UTC timestamp when reset was requested.</param>
        public AlarmResetRequestedEvent(string source, DateTime timestamp)
        {
            Source = source;
            Timestamp = timestamp;
            ResetAll = true;
            AlarmId = null;
        }

        /// <summary>
        /// Initializes a new instance for resetting a specific alarm.
        /// </summary>
        /// <param name="source">The source of the reset request.</param>
        /// <param name="alarmId">The specific alarm ID to reset.</param>
        /// <param name="timestamp">The UTC timestamp when reset was requested.</param>
        public AlarmResetRequestedEvent(string source, int alarmId, DateTime timestamp)
        {
            Source = source;
            Timestamp = timestamp;
            ResetAll = false;
            AlarmId = alarmId;
        }
    }
}
