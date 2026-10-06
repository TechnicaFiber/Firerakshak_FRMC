using System;

namespace FireRakshak.FRMC.Core.Events
{
    /// <summary>
    /// Event published when a relay command fails to execute (e.g., RS232 communication error).
    /// This event is typically published by the RelayControlManager.
    /// </summary>
    public class RelayCommandFailedEvent : IEvent
    {
        /// <summary>
        /// Gets the relay ID where the command failed.
        /// </summary>
        public int RelayId { get; }

        /// <summary>
        /// Gets the command that failed (e.g., "RELAY 1 ON").
        /// </summary>
        public string Command { get; }

        /// <summary>
        /// Gets the error message describing why the command failed.
        /// </summary>
        public string ErrorMessage { get; }

        /// <summary>
        /// Gets the timestamp when the failure occurred (UTC).
        /// </summary>
        public DateTime Timestamp { get; }

        /// <summary>
        /// Initializes a new instance of the RelayCommandFailedEvent class.
        /// </summary>
        public RelayCommandFailedEvent(int relayId, string command, string errorMessage, DateTime timestamp)
        {
            RelayId = relayId;
            Command = command;
            ErrorMessage = errorMessage;
            Timestamp = timestamp;
        }
    }
}
