namespace FireRakshak.FRMC.Core.Enums
{
    /// <summary>
    /// Defines various health parameters that are monitored within the FireRakshak FRMC system.
    /// These parameters provide insights into the operational status and well-being of different
    /// hardware and software components.
    /// </summary>
    public enum HealthParameter
    {
        /// <summary>
        /// Represents the operational status of a lamp or light source within a component.
        /// </summary>
        LampStatus,
        /// <summary>
        /// Represents the internal temperature of a specific module or component.
        /// </summary>
        ModuleTemperature,
        /// <summary>
        /// Represents the value of the Avalanche Photodiode (APD), crucial for optical signal detection.
        /// </summary>
        ApdValue,
        /// <summary>
        /// Represents the current flowing through the laser diode.
        /// </summary>
        LaserCurrent,
        /// <summary>
        /// Represents the current supplied to the pump laser.
        /// </summary>
        PumpCurrent,
        /// <summary>
        /// Indicates whether the DTS (Distributed Temperature Sensing) device is responsive and communicating.
        /// </summary>
        DtsResponsive
    }
}
