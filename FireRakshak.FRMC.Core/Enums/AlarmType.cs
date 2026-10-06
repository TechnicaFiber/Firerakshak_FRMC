namespace FireRakshak.FRMC.Core.Enums
{
    /// <summary>
    /// Defines the various types of alarms that can be triggered within the FireRakshak FRMC system.
    /// These types categorize alarm conditions for better management and response.
    /// </summary>
    public enum AlarmType
    {
        /// <summary>
        /// Indicates a pre-alarm condition, where a parameter is approaching a critical threshold.
        /// </summary>
        PreAlarm,
        /// <summary>
        /// Indicates that the maximum allowed temperature has been exceeded in a zone.
        /// </summary>
        MaxTemperature,
        /// <summary>
        /// Indicates that the minimum allowed temperature has been breached in a zone.
        /// </summary>
        MinTemperature,
        /// <summary>
        /// Indicates that the rate of temperature increase has exceeded a defined limit.
        /// </summary>
        RateOfRise,
        /// <summary>
        /// Indicates a significant deviation from expected temperature profiles within a zone.
        /// </summary>
        ZoneDeviation,
        /// <summary>
        /// Indicates a physical break or damage detected in the fiber optic cable.
        /// </summary>
        FiberBreak
    }
}
