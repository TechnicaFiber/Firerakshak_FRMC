namespace FireRakshak.FRMC.Core.Enums
{
    /// <summary>
    /// Types of laser control operations.
    /// </summary>
    public enum LaserControlType
    {
        /// <summary>
        /// Set EDFA pump current (mA).
        /// </summary>
        SetPumpCurrent,

        /// <summary>
        /// Set pulse width (ns).
        /// </summary>
        SetPulseWidth,

        /// <summary>
        /// Turn laser on.
        /// </summary>
        LaserOn,

        /// <summary>
        /// Turn laser off.
        /// </summary>
        LaserOff
    }
}
