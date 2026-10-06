using System;

namespace FireRakshak.FRMC.Core.Models;

public class DtsOptions
{
    public string IpAddress { get; set; } = "192.168.11.24";
    public int Port { get; set; } = 3001;
    public TimeSpan ConnectionRetryPeriod { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Delay in milliseconds between TCP commands to the DTS device.
    /// This is the delay after receiving a response before allowing the next command.
    /// Default: 100ms (matches original behavior).
    /// </summary>
    public int CommandDelayMs { get; set; } = 50;

    /// <summary>
    /// Number of worker threads for parallel temperature data processing.
    /// Default: 2 workers.
    /// </summary>
    public int PipelineWorkerCount { get; set; } = 2;

    /// <summary>
    /// Enable parallel processing of temperature data using producer-consumer pipeline.
    /// When false, falls back to sequential processing.
    /// Default: true.
    /// </summary>
    public bool EnableParallelProcessing { get; set; } = true;

    /// <summary>
    /// Delay in milliseconds between channel reads in parallel processing mode.
    /// This gives the DTS hardware time to prepare for the next read.
    /// Default: 100ms.
    /// </summary>
    public int InterReadDelayMs { get; set; } = 100;
}
