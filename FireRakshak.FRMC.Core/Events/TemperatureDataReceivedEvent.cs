using System;
using FireRakshak.FRMC.Core.Models;

namespace FireRakshak.FRMC.Core.Events
{
    /// <summary>
    /// Represents an event that is triggered when new temperature data is received from a DTS channel.
    /// This event is typically published by the <see cref="Managers.DtsManager.DtsManager"/>
    /// and consumed by the <see cref="Managers.ChannelsManager.ChannelsManager"/> for further processing.
    /// </summary>
    public class TemperatureDataReceivedEvent : IEvent
    {
        /// <summary>
        /// Gets or sets the unique identifier of the DTS channel from which the temperature data was received.
        /// </summary>
        public int ChannelId { get; set; }

        /// <summary>
        /// Gets or sets an array of <see cref="TemperatureDataPoint"/> objects,
        /// representing the temperature readings along the DTS fiber.
        /// </summary>
        public TemperatureDataPoint[] Data { get; set; }

        /// <summary>
        /// Gets or sets the timestamp when the temperature data was received (in UTC).
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="TemperatureDataReceivedEvent"/> class.
        /// </summary>
        /// <param name="channelId">The unique identifier of the channel.</param>
        /// <param name="data">An array of temperature data points.</param>
        /// <param name="timestamp">The UTC timestamp of the data reception.</param>
        public TemperatureDataReceivedEvent(int channelId, TemperatureDataPoint[] data, DateTime timestamp)
        {
            ChannelId = channelId;
            Data = data;
            Timestamp = timestamp;
        }
    }
}
