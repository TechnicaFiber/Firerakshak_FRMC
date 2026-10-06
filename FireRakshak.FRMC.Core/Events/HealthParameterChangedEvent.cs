using System;
using FireRakshak.FRMC.Core.Enums;

namespace FireRakshak.FRMC.Core.Events
{
    /// <summary>
    /// Represents an event that is triggered when a specific health parameter of the system changes.
    /// This event allows various components, particularly the <see cref="Managers.SystemHealthManager.SystemHealthManager"/>,
    /// to monitor and react to changes in the operational status of different parts of the system.
    /// </summary>
    public class HealthParameterChangedEvent : IEvent
    {
        /// <summary>
        /// Gets or sets the specific health parameter that has changed (e.g., DTS responsiveness, CPU usage).
        /// </summary>
        public HealthParameter Parameter { get; set; }

        /// <summary>
        /// Gets or sets the new value of the health parameter. This can be of any type,
        /// depending on the nature of the parameter (e.g., boolean for status, double for a metric).
        /// </summary>
        public object Value { get; set; }

        /// <summary>
        /// Gets or sets the timestamp when the health parameter change was detected (in UTC).
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="HealthParameterChangedEvent"/> class.
        /// </summary>
        /// <param name="parameter">The specific health parameter that changed.</param>
        /// <param name="value">The new value of the health parameter.</param>
        /// <param name="timestamp">The UTC timestamp of the change.</param>
        public HealthParameterChangedEvent(HealthParameter parameter, object value, DateTime timestamp)
        {
            Parameter = parameter;
            Value = value;
            Timestamp = timestamp;
        }
    }
}
