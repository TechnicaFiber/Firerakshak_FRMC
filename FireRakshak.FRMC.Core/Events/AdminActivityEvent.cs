using System;
using System.Collections.Generic;

namespace FireRakshak.FRMC.Core.Events
{
    /// <summary>
    /// Event triggered when an admin performs an action that should be audited.
    /// This event is logged to the event history for security and compliance purposes.
    /// </summary>
    public class AdminActivityEvent : IEvent
    {
        /// <summary>
        /// The action performed (e.g., "SetPumpCurrent", "UpdateIPConfiguration", "UpdateZoneConfiguration").
        /// </summary>
        public string Action { get; }

        /// <summary>
        /// The username of the admin who performed the action.
        /// </summary>
        public string Username { get; }

        /// <summary>
        /// The source of the command (e.g., "WebSocket", "RS232", "ModbusTCP").
        /// </summary>
        public string Source { get; }

        /// <summary>
        /// Additional details about the action (e.g., parameter values, target configuration).
        /// </summary>
        public Dictionary<string, object> Details { get; }

        /// <summary>
        /// The timestamp when the action occurred.
        /// </summary>
        public DateTime Timestamp { get; }

        /// <summary>
        /// Whether the action completed successfully.
        /// </summary>
        public bool Success { get; }

        /// <summary>
        /// Error message if the action failed.
        /// </summary>
        public string? ErrorMessage { get; }

        public AdminActivityEvent(
            string action,
            string username,
            string source,
            Dictionary<string, object> details,
            bool success = true,
            string? errorMessage = null)
        {
            Action = action;
            Username = username;
            Source = source;
            Details = details;
            Success = success;
            ErrorMessage = errorMessage;
            Timestamp = DateTime.UtcNow;
        }
    }
}
