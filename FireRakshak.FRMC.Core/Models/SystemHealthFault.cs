using System;
using FireRakshak.FRMC.Core.Enums;

namespace FireRakshak.FRMC.Core.Models
{
    /// <summary>
    /// Represents a system health fault with details about the condition.
    /// </summary>
    public class SystemHealthFault
    {
        /// <summary>
        /// Unique identifier for this fault
        /// </summary>
        public int FaultId { get; set; }

        /// <summary>
        /// Type of system health fault
        /// </summary>
        public SystemHealthFaultType FaultType { get; set; }

        /// <summary>
        /// Whether the fault is currently active
        /// </summary>
        public bool IsActive { get; set; }

        /// <summary>
        /// When the fault was first detected (UTC)
        /// </summary>
        public DateTime FirstDetectedAt { get; set; }

        /// <summary>
        /// When the fault was last updated (UTC)
        /// </summary>
        public DateTime LastUpdatedAt { get; set; }

        /// <summary>
        /// When the fault was cleared (UTC), null if still active
        /// </summary>
        public DateTime? ClearedAt { get; set; }

        /// <summary>
        /// Current value that triggered the fault
        /// </summary>
        public double CurrentValue { get; set; }

        /// <summary>
        /// Threshold value that was exceeded
        /// </summary>
        public double ThresholdValue { get; set; }

        /// <summary>
        /// Human-readable description of the fault
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Additional details or context
        /// </summary>
        public string? Details { get; set; }

        public SystemHealthFault()
        {
            Description = string.Empty;
        }
    }
}
