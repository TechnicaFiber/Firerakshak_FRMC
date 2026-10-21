using System;

namespace FireRakshak.FRMC.Core.Models
{
    /// <summary>
    /// Represents the overall system configuration settings.
    /// </summary>
    public class SystemConfiguration
    {
        public string IpAddress { get; set; }
        public string SubnetMask { get; set; }
        public string Gateway { get; set; }
        public DateTime SystemTime { get; set; }
        public string SoftwareVersion { get; set; }
        public int BackgroundServiceInterval { get; set; }

        // Relay/Zone Limits
        public int NumberOfRelays { get; set; } = 48;
        public int MaxZonesPerChannel { get; set; } = 1000;
        public double CorrectionLength { get; set; } = 0;


        // Timing Parameters (milliseconds)
        public int DtsPollingInterval { get; set; } = 6000;
        public int AlarmEvaluationInterval { get; set; } = 1000;
        public int AlarmStatusBroadcastInterval { get; set; } = 2000;
        public int HealthStatusBroadcastInterval { get; set; } = 5000;

        // Rate of Rise Calculation Window (seconds)
        // Sliding window duration for ROR calculation - industry standard is 60 seconds
        // Shorter windows (30s) = more sensitive to rapid changes but more noise
        // Longer windows (90s) = more stable but slower fire detection
        public int RateOfRiseWindowSeconds { get; set; } = 60;

        // Auto-Clear (seconds)
        public int AutoAlarmClearTimeout { get; set; } = 30;

        // System Health Monitoring Interval (seconds)
        // How often the system checks DTS communication, CPU load, memory, and module temperature
        // Default: 120 seconds (2 minutes)
        public int HealthMonitoringIntervalSeconds { get; set; } = 120;

        // ============================================================
        // SYSTEM HEALTH THRESHOLDS (Configurable from DTSCM)
        // These thresholds control when Relay 2 (System Health Fault) is triggered
        // ============================================================

        /// <summary>
        /// CPU usage threshold percentage. Fault raised when CPU > this value.
        /// Default: 90%
        /// </summary>
        public double CpuLoadThreshold { get; set; } = 90.0;

        /// <summary>
        /// Memory usage threshold percentage. Fault raised when memory > this value.
        /// Default: 90%
        /// </summary>
        public double MemoryLoadThreshold { get; set; } = 90.0;

        /// <summary>
        /// DTS module high temperature threshold (°C). Fault raised when temp > this value.
        /// Default: 70°C
        /// </summary>
        public double DtsModuleTempHighThreshold { get; set; } = 70.0;

        /// <summary>
        /// DTS module low temperature threshold (°C). Fault raised when temp < this value.
        /// Default: -10°C
        /// </summary>
        public double DtsModuleTempLowThreshold { get; set; } = -10.0;

        /// <summary>
        /// DTS communication timeout (seconds). Fault raised when no response for this duration.
        /// Default: 10 seconds
        /// </summary>
        public int DtsCommunicationTimeoutSeconds { get; set; } = 10;

        // DTS Point Resolution (meters)
        // Distance between consecutive temperature measurement points on the fiber.
        // This value depends on the DTS device configuration.
        // Default: 0.4 meters (configured on DTS device)
        // Common values: 0.25m, 0.4m, 0.5m, 1.0m
        public double DtsPointResolutionMeters { get; set; } = 0.4;

        // Decimation Settings for Live Data Streaming
        // Controls how much data is sent for large channels to optimize bandwidth
        /// <summary>
        /// Channel length threshold (km) for medium decimation. Channels >= this use medium decimation.
        /// Default: 3 km
        /// </summary>
        public double DecimationMediumThresholdKm { get; set; } = 3.0;

        /// <summary>
        /// Channel length threshold (km) for large decimation. Channels >= this use large decimation.
        /// Default: 6 km
        /// </summary>
        public double DecimationLargeThresholdKm { get; set; } = 6.0;

        /// <summary>
        /// Decimation factor for medium channels. Take 1 in N points.
        /// Default: 1 (no decimation — send all points)
        /// </summary>
        public int DecimationMediumFactor { get; set; } = 1;

        /// <summary>
        /// Decimation factor for large channels. Take 1 in N points.
        /// Default: 1 (no decimation — send all points)
        /// </summary>
        public int DecimationLargeFactor { get; set; } = 1;

        // ============================================================
        // LOGGING & RETENTION CONFIGURATION (Configurable from DTSCM)
        // ============================================================

        /// <summary>
        /// Enables automated ZIP archiving and storage capacity management for log files.
        /// Default: true
        /// </summary>
        public bool LogRetentionEnabled { get; set; } = true;

        /// <summary>
        /// Maximum retention age in days for historical log archives. Archives older than this will be pruned.
        /// Default: 14 days
        /// </summary>
        public int LogMaxAgeDays { get; set; } = 14;

        /// <summary>
        /// Maximum total storage quota for logs in MB.
        /// Default: 10240 MB (10 GB)
        /// </summary>
        public long LogTotalQuotaMB { get; set; } = 10240;

        /// <summary>
        /// High watermark storage ceiling in MB. When log storage exceeds this, oldest archives are pruned.
        /// Default: 9216 MB (9.0 GB)
        /// </summary>
        public long LogHighWatermarkMB { get; set; } = 9216;

        /// <summary>
        /// Low watermark storage target in MB after pruning.
        /// Default: 7680 MB (7.5 GB)
        /// </summary>
        public long LogLowWatermarkMB { get; set; } = 7680;

        /// <summary>
        /// Frequency in hours to check and archive logs.
        /// Default: 12 hours
        /// </summary>
        public int LogCheckIntervalHours { get; set; } = 12;

        /// <summary>
        /// Minimum logging severity level (Debug, Information, Warning, Error).
        /// Default: "Information"
        /// </summary>
        public string MinimumLogLevel { get; set; } = "Information";

        /// <summary>
        /// Parameterless constructor for JSON deserialization
        /// </summary>
        public SystemConfiguration()
        {
            IpAddress = "";
            SubnetMask = "";
            Gateway = "";
            SystemTime = DateTime.UtcNow;
            SoftwareVersion = "";
            BackgroundServiceInterval = 5000;
        }

        public SystemConfiguration(string ipAddress, string subnetMask, string gateway, DateTime systemTime, string softwareVersion, int backgroundServiceInterval)
        {
            IpAddress = ipAddress;
            SubnetMask = subnetMask;
            Gateway = gateway;
            SystemTime = systemTime;
            SoftwareVersion = softwareVersion;
            BackgroundServiceInterval = backgroundServiceInterval;
        }
    }
}
