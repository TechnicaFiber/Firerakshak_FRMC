using System;

namespace FireRakshak.FRMC.Core.Events
{
    /// <summary>
    /// Event raised when a fiber break is detected or cleared for a channel.
    /// </summary>
    public class FiberBreakDetectedEvent : IEvent
    {
        /// <summary>
        /// The channel ID where fiber break was detected or cleared
        /// </summary>
        public int ChannelId { get; }

        /// <summary>
        /// Indicates whether fiber is broken (true) or normal (false)
        /// </summary>
        public bool IsBroken { get; }

        /// <summary>
        /// Position (in meters) where the fiber is broken.
        /// If fiber is not broken, this indicates the fiber length.
        /// </summary>
        public double Position { get; }

        /// <summary>
        /// Timestamp when the event occurred (UTC)
        /// </summary>
        public DateTime Timestamp { get; }

        /// <summary>
        /// Initializes a new instance of the FiberBreakDetectedEvent.
        /// </summary>
        /// <param name="channelId">The channel ID</param>
        /// <param name="isBroken">True if fiber is broken, false if normal</param>
        /// <param name="position">Position in meters (break position or fiber length)</param>
        /// <param name="timestamp">UTC timestamp of the event</param>
        public FiberBreakDetectedEvent(int channelId, bool isBroken, double position, DateTime timestamp)
        {
            ChannelId = channelId;
            IsBroken = isBroken;
            Position = position;
            Timestamp = timestamp;
        }
    }
}
