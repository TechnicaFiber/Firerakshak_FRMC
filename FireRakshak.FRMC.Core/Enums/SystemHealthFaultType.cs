namespace FireRakshak.FRMC.Core.Enums
{
    /// <summary>
    /// Represents the type of system health fault detected.
    /// </summary>
    public enum SystemHealthFaultType
    {
        /// <summary>
        /// No fault - system healthy
        /// </summary>
        None = 0,

        /// <summary>
        /// DTS device not responding to communication
        /// </summary>
        DtsCommunicationLost = 1,

        /// <summary>
        /// DTS module temperature exceeds threshold
        /// </summary>
        DtsTemperatureHigh = 2,

        /// <summary>
        /// DTS module temperature below threshold
        /// </summary>
        DtsTemperatureLow = 3,

        /// <summary>
        /// CPU load exceeds threshold
        /// </summary>
        CpuLoadHigh = 4,

        /// <summary>
        /// Memory usage exceeds threshold
        /// </summary>
        MemoryLoadHigh = 5,

        /// <summary>
        /// Disk space critically low
        /// </summary>
        DiskSpaceLow = 6,

        /// <summary>
        /// Laser/pump is off (pump current = 0)
        /// </summary>
        LaserOff = 7,

        /// <summary>
        /// General system fault
        /// </summary>
        SystemFault = 99
    }
}
