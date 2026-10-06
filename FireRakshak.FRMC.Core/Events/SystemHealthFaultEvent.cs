using System;
using FireRakshak.FRMC.Core.Enums;
using FireRakshak.FRMC.Core.Models;

namespace FireRakshak.FRMC.Core.Events
{
    /// <summary>
    /// Event raised when a system health fault is detected or cleared.
    /// </summary>
    public class SystemHealthFaultEvent : IEvent
    {
        /// <summary>
        /// The health fault details
        /// </summary>
        public SystemHealthFault Fault { get; }

        /// <summary>
        /// Whether this is a new fault (true) or a fault clearing (false)
        /// </summary>
        public bool IsNewFault { get; }

        /// <summary>
        /// Timestamp when the event occurred (UTC)
        /// </summary>
        public DateTime Timestamp { get; }

        /// <summary>
        /// Initializes a new instance of the SystemHealthFaultEvent.
        /// </summary>
        /// <param name="fault">The health fault details</param>
        /// <param name="isNewFault">True if new fault, false if fault cleared</param>
        /// <param name="timestamp">UTC timestamp of the event</param>
        public SystemHealthFaultEvent(SystemHealthFault fault, bool isNewFault, DateTime timestamp)
        {
            Fault = fault;
            IsNewFault = isNewFault;
            Timestamp = timestamp;
        }
    }
}
