using System;

namespace FireRakshak.FRMC.Core.Models
{
    /// <summary>
    /// Represents the current health parameters of the DTS system.
    /// </summary>
    public class HealthParameters
    {
        public bool LampStatus { get; set; }
        public bool DtsResponsive { get; set; }
        public double ModuleTemperature { get; set; }
        public double ApdValue { get; set; }
        public double LaserCurrent { get; set; }
        public double PumpCurrent { get; set; }
        public DateTime LastCheck { get; set; }

        // System resource monitoring
        public double CpuUsage { get; set; }
        public double MemoryUsage { get; set; }
        public double MemoryUsedGB { get; set; }
        public double MemoryTotalGB { get; set; }

        public HealthParameters(bool lampStatus, bool dtsResponsive, double moduleTemperature, double apdValue, double laserCurrent, double pumpCurrent, DateTime lastCheck)
        {
            LampStatus = lampStatus;
            DtsResponsive = dtsResponsive;
            ModuleTemperature = moduleTemperature;
            ApdValue = apdValue;
            LaserCurrent = laserCurrent;
            PumpCurrent = pumpCurrent;
            LastCheck = lastCheck;
        }
    }
}
