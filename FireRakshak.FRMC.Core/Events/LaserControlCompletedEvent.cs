using FireRakshak.FRMC.Core.Enums;
using System;

namespace FireRakshak.FRMC.Core.Events
{
    /// <summary>
    /// Event published when a laser control operation has completed.
    /// </summary>
    public class LaserControlCompletedEvent : IEvent
    {
        /// <summary>
        /// The type of laser control operation that was executed.
        /// </summary>
        public LaserControlType ControlType { get; }

        /// <summary>
        /// The value that was set (pump current in mA or pulse width in ns).
        /// </summary>
        public double Value { get; }

        /// <summary>
        /// Indicates whether the operation was successful.
        /// </summary>
        public bool Success { get; }

        /// <summary>
        /// Error message if the operation failed.
        /// </summary>
        public string? ErrorMessage { get; }

        /// <summary>
        /// Timestamp when the operation completed.
        /// </summary>
        public DateTime Timestamp { get; }

        public LaserControlCompletedEvent(LaserControlType controlType, double value, bool success, string? errorMessage = null)
        {
            ControlType = controlType;
            Value = value;
            Success = success;
            ErrorMessage = errorMessage;
            Timestamp = DateTime.UtcNow;
        }
    }
}
