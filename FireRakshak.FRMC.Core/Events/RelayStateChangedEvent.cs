using System;
using FireRakshak.FRMC.Core.Models;

namespace FireRakshak.FRMC.Core.Events
{
    /// <summary>
    /// Event published when a relay's state changes (ON/OFF or Auto/Manual mode).
    /// This event is typically published by the RelayControlManager.
    /// </summary>
    public class RelayStateChangedEvent : IEvent
    {
        /// <summary>
        /// Gets the relay ID that changed state.
        /// </summary>
        public int RelayId { get; }

        /// <summary>
        /// Gets the new state of the relay (true = ON, false = OFF).
        /// </summary>
        public bool NewState { get; }

        /// <summary>
        /// Gets how the relay is controlled (Auto or Manual).
        /// </summary>
        public RelayControlSource Source { get; }

        /// <summary>
        /// Gets the username or system identifier that caused the change.
        /// </summary>
        public string ChangedBy { get; }

        /// <summary>
        /// Gets the timestamp when the state changed (UTC).
        /// </summary>
        public DateTime Timestamp { get; }

        /// <summary>
        /// Initializes a new instance of the RelayStateChangedEvent class.
        /// </summary>
        public RelayStateChangedEvent(int relayId, bool newState, RelayControlSource source, string changedBy, DateTime timestamp)
        {
            RelayId = relayId;
            NewState = newState;
            Source = source;
            ChangedBy = changedBy;
            Timestamp = timestamp;
        }
    }
}
