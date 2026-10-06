using FireRakshak.FRMC.Core.Enums;
using System;

namespace FireRakshak.FRMC.Core.Events
{
    /// <summary>
    /// Event published when a laser control command is requested via WebSocket or RS232.
    /// </summary>
    public class LaserControlRequestedEvent : IEvent
    {
        /// <summary>
        /// The type of laser control operation.
        /// </summary>
        public LaserControlType ControlType { get; }

        /// <summary>
        /// The value to set (pump current in mA or pulse width in ns).
        /// </summary>
        public double Value { get; }

        /// <summary>
        /// Source of the command (WebSocket, RS232, etc.).
        /// </summary>
        public string Source { get; }

        /// <summary>
        /// Timestamp when the control request was made.
        /// </summary>
        public DateTime Timestamp { get; }

        public LaserControlRequestedEvent(LaserControlType controlType, double value, string source)
        {
            ControlType = controlType;
            Value = value;
            Source = source;
            Timestamp = DateTime.UtcNow;
        }
    }
}
