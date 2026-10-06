using System;

namespace FireRakshak.FRMC.Core.Models
{
    /// <summary>
    /// Represents the current state of a DTS channel.
    /// </summary>
    public class ChannelState
    {
        public int ChannelId { get; set; }

        /// <summary>
        /// Configured fiber length (in meters) - the expected/designed length.
        /// </summary>
        public double ConfiguredLength { get; set; }

        /// <summary>
        /// Actual fiber length (in meters) - equals ConfiguredLength if fiber is OK,
        /// or equals FiberBreakPosition if fiber is broken.
        /// </summary>
        public double ActualLength { get; set; }
        public double CorrectionLength { get; set; }
        public TemperatureDataPoint[] CurrentData { get; set; }
        public bool IsActive { get; set; }
        public DateTime LastUpdate { get; set; }
        public int NumberOfZones { get; set; }

        /// <summary>
        /// Timestamp of the last alarm event for this channel.
        /// Null if no alarm has occurred or all alarms have been cleared.
        /// </summary>
        public DateTime? LastAlarmTime { get; set; }

        /// <summary>
        /// Indicates whether the fiber for this channel is broken.
        /// </summary>
        public bool FiberBroken { get; set; }

        /// <summary>
        /// Position (in meters) where the fiber is broken.
        /// Null if fiber is not broken.
        /// </summary>
        public double? FiberBreakPosition { get; set; }

        public ChannelState(int channelId, double configuredLength, double correctionLength, TemperatureDataPoint[] currentData, bool isActive, DateTime lastUpdate, int numberOfZones = 0)
        {
            ChannelId = channelId;
            ConfiguredLength = configuredLength;
            ActualLength = configuredLength; // Initially equals configured length
            CorrectionLength = correctionLength;
            CurrentData = currentData;
            IsActive = isActive;
            LastUpdate = lastUpdate;
            NumberOfZones = numberOfZones;
            LastAlarmTime = null;
            FiberBroken = false;
            FiberBreakPosition = null;
        }
    }
}
