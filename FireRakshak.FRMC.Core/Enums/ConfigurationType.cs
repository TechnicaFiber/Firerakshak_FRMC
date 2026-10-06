namespace FireRakshak.FRMC.Core.Enums
{
    /// <summary>
    /// Defines the categories of configuration settings that can be managed or changed within the system.
    /// This helps in categorizing configuration updates and targeting specific parts of the system.
    /// </summary>
    public enum ConfigurationType
    {
        /// <summary>
        /// Represents global system-wide configuration settings.
        /// </summary>
        System,
        /// <summary>
        /// Represents configuration settings specific to the DTS (Distributed Temperature Sensing) device.
        /// </summary>
        Dts,
        /// <summary>
        /// Represents configuration settings for individual DTS channels.
        /// </summary>
        Channel,
        /// <summary>
        /// Represents configuration settings for specific zones within DTS channels.
        /// </summary>
        Zone,
        /// <summary>
        /// Represents configuration settings for alarm thresholds and behaviors.
        /// </summary>
        Alarm,
        Network

    }
}
