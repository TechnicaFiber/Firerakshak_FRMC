using System;

namespace FireRakshak.FRMC.Core.Models
{
    /// <summary>
    /// Indicates how a relay's state is controlled.
    /// </summary>
    public enum RelayControlSource
    {
        /// <summary>
        /// Relay is controlled automatically by alarm events
        /// </summary>
        Auto,

        /// <summary>
        /// Relay is controlled manually by an operator
        /// </summary>
        Manual
    }

    /// <summary>
    /// Represents the current state of a relay in the system.
    ///
    /// IMPORTANT: Relay-to-zone assignments are NOT tracked here to support 1:many relationships.
    /// Instead, assignments are tracked in ZoneConfiguration.AssignedRelayId, which allows
    /// multiple zones to share the same relay.
    /// </summary>
    public class RelayState
    {
        /// <summary>
        /// Relay identifier (1-based, matches physical relay board addressing)
        /// </summary>
        public int RelayId { get; set; }

        /// <summary>
        /// Current state of the relay (true = ON, false = OFF)
        /// </summary>
        public bool IsOn { get; set; }

        /// <summary>
        /// How the relay is currently controlled (Auto or Manual)
        /// </summary>
        public RelayControlSource ControlSource { get; set; }

        /// <summary>
        /// Timestamp of the last state change (UTC)
        /// </summary>
        public DateTime LastStateChange { get; set; }

        /// <summary>
        /// Username or system identifier that last changed the relay state
        /// </summary>
        public string LastChangedBy { get; set; } = "System";
    }
}
