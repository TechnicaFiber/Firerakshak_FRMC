using System;
using FireRakshak.FRMC.Core.Models;

namespace FireRakshak.FRMC.Core.Events
{
    /// <summary>
    /// Represents an event that is triggered when a zone's state has been updated with new temperature metrics.
    /// This event is published by the <see cref="Managers.ChannelsManager.ChannelsManager"/>
    /// after processing temperature data and updating zone statistics (average, max, RoR, deviation).
    /// It is consumed by the Modbus TCP server to update register values in real-time.
    /// </summary>
    public class ZoneStateUpdatedEvent : IEvent
    {
        /// <summary>
        /// Gets the unique identifier of the channel to which this zone belongs.
        /// </summary>
        public int ChannelId { get; }

        /// <summary>
        /// Gets the unique identifier of the zone whose state was updated.
        /// </summary>
        public int ZoneId { get; }

        /// <summary>
        /// Gets the updated zone state containing current temperature metrics.
        /// </summary>
        public ZoneState State { get; }

        /// <summary>
        /// Gets the timestamp when this zone state update occurred (in UTC).
        /// </summary>
        public DateTime Timestamp { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="ZoneStateUpdatedEvent"/> class.
        /// </summary>
        /// <param name="channelId">The channel identifier.</param>
        /// <param name="zoneId">The zone identifier.</param>
        /// <param name="state">The updated zone state.</param>
        /// <param name="timestamp">The UTC timestamp of the update.</param>
        public ZoneStateUpdatedEvent(int channelId, int zoneId, ZoneState state, DateTime timestamp)
        {
            ChannelId = channelId;
            ZoneId = zoneId;
            State = state;
            Timestamp = timestamp;
        }
    }
}
