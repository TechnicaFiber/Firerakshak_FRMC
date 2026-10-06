using FireRakshak.FRMC.Core.Enums;
using FireRakshak.FRMC.Core.EventAggregator;
using FireRakshak.FRMC.Core.Events;
using FireRakshak.FRMC.Core.Models;
using FireRakshak.FRMC.Managers.RS232Manager;
using FireRakshak.FRMC.Managers.SystemConfigurationManager;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FireRakshak.FRMC.Managers.SystemHealthManager;

/// <summary>
/// Manages the aggregated health state of the entire system. It listens for individual
/// health parameter change events from various components and maintains a consolidated
/// view of the system's health.
/// </summary>
public class SystemHealthManager : ISystemHealthManager, IDisposable
{
    private readonly IEventAggregator _eventAggregator;
    private readonly ILogger<SystemHealthManager> _logger;
    private readonly SystemHealthFaultLogger _faultLogger;
    private readonly IConfigurationManager _configurationManager;
    private HealthParameters _currentHealth; // In-memory cache for the current system health.

    // System health monitoring
    private readonly ConcurrentDictionary<SystemHealthFaultType, SystemHealthFault> _activeFaults = new();
    private int _nextFaultId = 1;
    private DateTime _lastDtsCommunication = DateTime.MinValue;
    private Timer? _monitoringTimer;
    private readonly SemaphoreSlim _monitoringSemaphore = new(1, 1); // Prevent concurrent monitoring
    private PerformanceCounter? _cpuCounter; // Cached PerformanceCounter
    private bool _disposed = false;
    private int _currentMonitoringIntervalSeconds; // Tracks current interval for change detection
    private readonly EventHistoryService _eventHistoryService;

    // Rolling average for CPU and memory (avoids false faults from temporary spikes)
    private const int ROLLING_AVERAGE_SAMPLES = 5; // 5 samples = ~10 minutes at 2-minute interval
    private readonly Queue<double> _cpuReadings = new();
    private readonly Queue<double> _memoryReadings = new();


    // Default thresholds (used if configuration is unavailable)
    private const double DEFAULT__dtsModuleTempHighThreshold = 70.0; // °C
    private const double DEFAULT__dtsModuleTempLowThreshold = -10.0; // °C
    private const double DEFAULT__cpuLoadThreshold = 90.0; // %
    private const double DEFAULT__memoryLoadThreshold = 90.0; // %
    private const int DEFAULT__dtsCommunicationTimeoutSeconds = 10; // seconds
    private const int DEFAULT_HEALTH_MONITORING_INTERVAL_SECONDS = 120; // Default: 2 minutes

    // Cached configurable thresholds (loaded from configuration)
    private double _cpuLoadThreshold = DEFAULT__cpuLoadThreshold;
    private double _memoryLoadThreshold = DEFAULT__memoryLoadThreshold;
    private double _dtsModuleTempHighThreshold = DEFAULT__dtsModuleTempHighThreshold;
    private double _dtsModuleTempLowThreshold = DEFAULT__dtsModuleTempLowThreshold;
    private int _dtsCommunicationTimeoutSeconds = DEFAULT__dtsCommunicationTimeoutSeconds;
    private long _prevIdleTime = 0;
    private long _prevTotalTime = 0;
    /// <summary>
    /// Initializes a new instance of the <see cref="SystemHealthManager"/> class.
    /// </summary>
    /// <param name="eventAggregator">The event aggregator for subscribing to health-related events.</param>
    /// <param name="logger">The logger for recording operational information.</param>
    /// <param name="faultLogger">The fault logger for recording health faults.</param>
    /// <param name="configurationManager">The configuration manager for accessing system settings.</param>
    public SystemHealthManager(
        IEventAggregator eventAggregator,
        ILogger<SystemHealthManager> logger,
        SystemHealthFaultLogger faultLogger,
        IConfigurationManager configurationManager,
        EventHistoryService eventHistoryService)
    {
        _eventAggregator = eventAggregator;
        _logger = logger;
        _faultLogger = faultLogger;
        _configurationManager = configurationManager;
        _eventHistoryService = eventHistoryService;

        // Initialize with a default 'unhealthy' state until the first health reports come in.
        _currentHealth = new HealthParameters(false, false, 0, 0, 0, 0, DateTime.MinValue);

        // Subscribe to events that report changes in specific health parameters.
        _eventAggregator.Subscribe<HealthParameterChangedEvent>(HandleHealthParameterChangedEvent);
        _eventAggregator.Subscribe<TemperatureDataReceivedEvent>(HandleDtsCommunication);
        _eventAggregator.Subscribe<FiberBreakDetectedEvent>(HandleFiberBreakCommunication);
        _eventAggregator.Subscribe<ConfigurationChangedEvent>(HandleConfigurationChanged);

        // Load thresholds from configuration
        LoadHealthThresholdsFromConfig();

        // Load monitoring interval from configuration (or use default)
        _currentMonitoringIntervalSeconds = GetHealthMonitoringIntervalSeconds();

        // Start background monitoring with configurable interval
        _monitoringTimer = new Timer(
            MonitorSystemHealth,
            null,
            TimeSpan.FromSeconds(5), // Start after 5 seconds (reduced from 10 for faster initial reading)
            TimeSpan.FromSeconds(_currentMonitoringIntervalSeconds));

        _logger.LogInformation(
            "SystemHealthManager initialized with health monitoring interval of {Interval} seconds",
            _currentMonitoringIntervalSeconds);

        // Trigger immediate CPU/memory check on startup (fire and forget)
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1000); // Wait 1 second for system to stabilize
                await CheckCpuLoadAsync();
                await CheckMemoryLoadAsync();
                RefreshHealthPayload();
                _logger.LogInformation("Initial CPU/memory check completed");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Initial CPU/memory check failed");
            }
        });
    }

    /// <summary>
    /// Gets the health monitoring interval from system configuration.
    /// Returns default of 120 seconds if not configured.
    /// </summary>
    private int GetHealthMonitoringIntervalSeconds()
    {
        try
        {
            var config = _configurationManager.GetSystemConfigurationAsync()
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();

            int interval = config?.HealthMonitoringIntervalSeconds ?? DEFAULT_HEALTH_MONITORING_INTERVAL_SECONDS;

            // Validate: minimum 10 seconds, maximum 3600 seconds (1 hour)
            if (interval < 10)
            {
                _logger.LogWarning(
                    "HealthMonitoringIntervalSeconds ({Interval}) is too low, using minimum of 10 seconds",
                    interval);
                return 10;
            }

            if (interval > 3600)
            {
                _logger.LogWarning(
                    "HealthMonitoringIntervalSeconds ({Interval}) is too high, using maximum of 3600 seconds",
                    interval);
                return 3600;
            }

            return interval;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to load HealthMonitoringIntervalSeconds from config, using default {Default} seconds",
                DEFAULT_HEALTH_MONITORING_INTERVAL_SECONDS);
            return DEFAULT_HEALTH_MONITORING_INTERVAL_SECONDS;
        }
    }

    /// <summary>
    /// Loads health thresholds from system configuration.
    /// Called at startup and when configuration changes.
    /// </summary>
    private void LoadHealthThresholdsFromConfig()
    {
        try
        {
            var config = _configurationManager.GetSystemConfigurationAsync()
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();

            if (config == null)
            {
                _logger.LogWarning("System configuration not available, using default health thresholds");
                return;
            }

            // Load CPU threshold
            if (config.CpuLoadThreshold > 0 && config.CpuLoadThreshold <= 100)
            {
                _cpuLoadThreshold = config.CpuLoadThreshold;
            }

            // Load Memory threshold
            if (config.MemoryLoadThreshold > 0 && config.MemoryLoadThreshold <= 100)
            {
                _memoryLoadThreshold = config.MemoryLoadThreshold;
            }

            // Load DTS Temperature High threshold
            if (config.DtsModuleTempHighThreshold > 0)
            {
                _dtsModuleTempHighThreshold = config.DtsModuleTempHighThreshold;
            }

            // Load DTS Temperature Low threshold
            _dtsModuleTempLowThreshold = config.DtsModuleTempLowThreshold;

            // Load DTS Communication Timeout
            if (config.DtsCommunicationTimeoutSeconds > 0)
            {
                _dtsCommunicationTimeoutSeconds = config.DtsCommunicationTimeoutSeconds;
            }

            _logger.LogInformation(
                "Health thresholds loaded: CPU={CpuThreshold}%, Memory={MemoryThreshold}%, " +
                "TempHigh={TempHigh}°C, TempLow={TempLow}°C, CommTimeout={Timeout}s",
                _cpuLoadThreshold, _memoryLoadThreshold,
                _dtsModuleTempHighThreshold, _dtsModuleTempLowThreshold,
                _dtsCommunicationTimeoutSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load health thresholds from config, using defaults");
        }
    }

    /// <summary>
    /// Handles configuration changes to update the monitoring interval and thresholds dynamically.
    /// </summary>
    private Task HandleConfigurationChanged(ConfigurationChangedEvent ev)
    {
        if (ev.ConfigType != ConfigurationType.System)
            return Task.CompletedTask;

        // Check for monitoring interval change
        if (ev.Changes.ContainsKey("HealthMonitoringIntervalSeconds"))
        {
            int newInterval = Convert.ToInt32(ev.Changes["HealthMonitoringIntervalSeconds"]);

            // Validate interval
            if (newInterval < 10) newInterval = 10;
            if (newInterval > 3600) newInterval = 3600;

            if (newInterval != _currentMonitoringIntervalSeconds)
            {
                _logger.LogInformation(
                    "Health monitoring interval changed from {OldInterval} to {NewInterval} seconds",
                    _currentMonitoringIntervalSeconds, newInterval);

                _currentMonitoringIntervalSeconds = newInterval;

                // Update timer with new interval
                _monitoringTimer?.Change(
                    TimeSpan.FromSeconds(newInterval), // Due time (start next cycle with new interval)
                    TimeSpan.FromSeconds(newInterval)); // Period
            }
        }

        // Check for threshold changes and reload all thresholds
        var thresholdKeys = new[]
        {
            "CpuLoadThreshold", "MemoryLoadThreshold",
            "DtsModuleTempHighThreshold", "DtsModuleTempLowThreshold",
            "DtsCommunicationTimeoutSeconds"
        };

        if (thresholdKeys.Any(k => ev.Changes.ContainsKey(k)))
        {
            _logger.LogInformation("Health thresholds configuration changed, reloading thresholds");
            LoadHealthThresholdsFromConfig();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<HealthParameters> GetCurrentHealthAsync()
    {
        _logger.LogInformation("Retrieving current system health.");

        // Refresh CPU and memory on demand for accurate values
        await RefreshCpuAndMemoryAsync();

        return _currentHealth;
    }

    /// <summary>
    /// Refreshes CPU and memory values on demand (for real-time requests).
    /// CPU PerformanceCounter requires two reads with a delay to calculate accurate percentage.
    /// </summary>
    //private async Task RefreshCpuAndMemoryAsync()
    //{
    //    try
    //    {
    //        // CPU - always need two reads with delay for accurate measurement
    //        _cpuCounter ??= new PerformanceCounter("Processor", "% Processor Time", "_Total");
    //        _cpuCounter.NextValue(); // First read (baseline)
    //        await Task.Delay(250); // Wait for sample interval
    //        float cpuLoad = _cpuCounter.NextValue(); // Second read (actual value)
    //        _currentHealth.CpuUsage = cpuLoad;

    //        // Memory - point-in-time read (no delay needed)
    //        var gcInfo = GC.GetGCMemoryInfo();
    //        var totalPhysicalMemory = (double)gcInfo.TotalAvailableMemoryBytes;

    //        double availableMemoryBytes;
    //        try
    //        {
    //            using var availableMemCounter = new PerformanceCounter("Memory", "Available Bytes");
    //            availableMemoryBytes = availableMemCounter.NextValue();
    //        }
    //        catch
    //        {
    //            availableMemoryBytes = totalPhysicalMemory * 0.3;
    //        }

    //        var usedMemoryBytes = totalPhysicalMemory - availableMemoryBytes;
    //        var memoryLoadPercent = (usedMemoryBytes / totalPhysicalMemory) * 100;

    //        if (memoryLoadPercent < 0) memoryLoadPercent = 0;
    //        if (memoryLoadPercent > 100) memoryLoadPercent = 100;

    //        _currentHealth.MemoryUsage = memoryLoadPercent;
    //        _currentHealth.MemoryUsedGB = usedMemoryBytes / 1024 / 1024 / 1024;
    //        _currentHealth.MemoryTotalGB = totalPhysicalMemory / 1024 / 1024 / 1024;

    //        // Refresh payload
    //        RefreshHealthPayload();
    //    }
    //    catch (Exception ex)
    //    {
    //        _logger.LogWarning(ex, "Error refreshing CPU/memory on demand");
    //    }
    //}
    private async Task RefreshCpuAndMemoryAsync()
    {
        try
        {
#if WINDOWS
        // ---------- WINDOWS ----------
        _cpuCounter ??= new PerformanceCounter("Processor", "% Processor Time", "_Total");
        _cpuCounter.NextValue();
        await Task.Delay(250);
        float cpuLoad = _cpuCounter.NextValue();
        _currentHealth.CpuUsage = cpuLoad;

        var gcInfo = GC.GetGCMemoryInfo();
        var totalMemoryBytes = (double)gcInfo.TotalAvailableMemoryBytes;

        double availableMemoryBytes;
        try
        {
            using var availableMemCounter =
                new PerformanceCounter("Memory", "Available Bytes");
            availableMemoryBytes = availableMemCounter.NextValue();
        }
        catch
        {
            availableMemoryBytes = totalMemoryBytes * 0.3;
        }

#else
            // ---------- LINUX ----------
            // CPU from /proc/stat
            var firstLine = System.IO.File.ReadLines("/proc/stat").First();
            var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            long user = long.Parse(parts[1]);
            long nice = long.Parse(parts[2]);
            long system = long.Parse(parts[3]);
            long idle = long.Parse(parts[4]);
            long iowait = parts.Length > 5 ? long.Parse(parts[5]) : 0;

            long total = user + nice + system + idle + iowait;
            long idleTotal = idle + iowait;

            if (_prevTotalTime != 0)
            {
                long totalDiff = total - _prevTotalTime;
                long idleDiff = idleTotal - _prevIdleTime;

                float cpuLoad = totalDiff > 0
                    ? (1f - (float)idleDiff / totalDiff) * 100f
                    : 0f;

                _currentHealth.CpuUsage = cpuLoad;
            }

            _prevTotalTime = total;
            _prevIdleTime = idleTotal;

            // Memory from /proc/meminfo
            var memInfo = System.IO.File.ReadAllLines("/proc/meminfo");

            long memTotalKb = 0;
            long memAvailableKb = 0;

            foreach (var line in memInfo)
            {
                if (line.StartsWith("MemTotal:"))
                    memTotalKb = long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
                else if (line.StartsWith("MemAvailable:"))
                    memAvailableKb = long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
            }

            var totalMemoryBytes = memTotalKb * 1024.0;
            var availableMemoryBytes = memAvailableKb * 1024.0;
#endif

            if (totalMemoryBytes > 0)
            {
                var usedMemoryBytes = totalMemoryBytes - availableMemoryBytes;
                var memoryLoadPercent = (usedMemoryBytes / totalMemoryBytes) * 100;

                memoryLoadPercent = Math.Clamp(memoryLoadPercent, 0, 100);

                _currentHealth.MemoryUsage = memoryLoadPercent;
                _currentHealth.MemoryUsedGB = usedMemoryBytes / 1024 / 1024 / 1024;
                _currentHealth.MemoryTotalGB = totalMemoryBytes / 1024 / 1024 / 1024;
            }

            RefreshHealthPayload();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error refreshing CPU/memory");
        }
    }


    /// <inheritdoc/>
    public JsonElement CurrentHealthPayload { get; private set; }

    public async Task UpdateHealthParametersAsync(HealthParameters health)
    {
        _logger.LogInformation("System health parameters updated.");

        _currentHealth = health;

        // Refresh the payload with all current values including CPU/memory
        RefreshHealthPayload();

        await Task.CompletedTask;
    }

    /// <summary>
    /// Handles the <see cref="HealthParameterChangedEvent"/> to update the aggregated health state.
    /// Any health parameter received from DTS means communication is working.
    /// </summary>
    private async Task HandleHealthParameterChangedEvent(HealthParameterChangedEvent ev)
    {
        _logger.LogDebug("Health parameter {Parameter} changed to {Value}", ev.Parameter, ev.Value);

        // Any health parameter received means DTS communication is working
        await MarkDtsAsResponsiveAsync();

        switch (ev.Parameter)
        {
            case Core.Enums.HealthParameter.LampStatus:
                _currentHealth.LampStatus = (bool)ev.Value;
                break;

            case Core.Enums.HealthParameter.DtsResponsive:
                _currentHealth.DtsResponsive = (bool)ev.Value;
                break;

            case Core.Enums.HealthParameter.ModuleTemperature:
                _currentHealth.ModuleTemperature = Convert.ToDouble(ev.Value);
                break;

            case Core.Enums.HealthParameter.ApdValue:
                _currentHealth.ApdValue = Convert.ToDouble(ev.Value);
                break;

            case Core.Enums.HealthParameter.LaserCurrent:
                {
                    double laserCurrent = Convert.ToDouble(ev.Value);
                    _currentHealth.LaserCurrent = laserCurrent;

                    
                    _currentHealth.LampStatus = laserCurrent > 0;

                    break;
                }





            case Core.Enums.HealthParameter.PumpCurrent:
                {
                    double pumpCurrent = Convert.ToDouble(ev.Value);
                    _currentHealth.PumpCurrent = pumpCurrent;

                    // Raise or clear LaserOff fault based on pump current
                    if (pumpCurrent <= 0)
                    {
                        _ = RaiseFaultAsync(
                            SystemHealthFaultType.LaserOff,
                            pumpCurrent,
                            0,
                            "Laser is OFF (pump current = 0)",
                            "Pump current must be > 0 for laser to be active");
                    }
                    else if (_activeFaults.ContainsKey(SystemHealthFaultType.LaserOff))
                    {
                        _ = ClearFaultAsync(SystemHealthFaultType.LaserOff);
                    }
                    break;
                }
        }

        _currentHealth.LastCheck = ev.Timestamp;

        // Refresh JSON payload
        RefreshHealthPayload();
    }

    /// <summary>
    /// Handles DTS temperature data communication - updates last communication timestamp.
    /// </summary>
    private async Task HandleDtsCommunication(TemperatureDataReceivedEvent ev)
    {
        await MarkDtsAsResponsiveAsync();
    }

    /// <summary>
    /// Handles DTS fiber break communication - updates last communication timestamp.
    /// Any communication from DTS (temperature or fiber break) indicates DTS is responsive.
    /// </summary>
    private async Task HandleFiberBreakCommunication(FiberBreakDetectedEvent ev)
    {
        await MarkDtsAsResponsiveAsync();
    }

    /// <summary>
    /// Marks DTS as responsive - called when any communication is received from DTS.
    /// Also clears the DtsCommunicationLost fault immediately if it exists.
    /// </summary>
    private async Task MarkDtsAsResponsiveAsync()
    {
        _lastDtsCommunication = DateTime.UtcNow;
        _currentHealth.DtsResponsive = true;
        _currentHealth.LastCheck = DateTime.UtcNow;

        // Clear the fault immediately when we receive actual communication
        if (_activeFaults.ContainsKey(SystemHealthFaultType.DtsCommunicationLost))
        {
            await ClearFaultAsync(SystemHealthFaultType.DtsCommunicationLost);
        }

        RefreshHealthPayload();
    }

    /// <summary>
    /// Background monitoring method - checks all health parameters.
    /// Protected by semaphore to prevent concurrent execution.
    /// </summary>
    private async void MonitorSystemHealth(object? state)
    {
        // Non-blocking check - skip if previous monitoring is still running
        if (!await _monitoringSemaphore.WaitAsync(0))
        {
            _logger.LogWarning("System health monitoring still running, skipping this cycle");
            return;
        }

        try
        {
            await CheckDtsCommunicationAsync();
            await CheckDtsModuleTemperatureAsync();
            await CheckCpuLoadAsync();
            await CheckMemoryLoadAsync();

            // Always refresh the health payload after all checks complete
            RefreshHealthPayload();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in system health monitoring");
        }
        finally
        {
            _monitoringSemaphore.Release();
        }
    }

    /// <summary>
    /// Checks if DTS is responding within timeout.
    /// </summary>
   
    private async Task CheckDtsCommunicationAsync()
    {
        // DTS communication is tracked via any message received from DTS:
        // - Temperature data (TemperatureDataReceivedEvent)
        // - Fiber break notifications (FiberBreakDetectedEvent)
        // Any of these mark DTS as responsive.

        var timeSinceLastComm = DateTime.UtcNow - _lastDtsCommunication;
        bool neverReceived = _lastDtsCommunication == DateTime.MinValue;

        bool isCommLost = neverReceived || timeSinceLastComm.TotalSeconds > _dtsCommunicationTimeoutSeconds;

        if (isCommLost)
        {
            _currentHealth.DtsResponsive = false;
            _currentHealth.LastCheck = DateTime.UtcNow;

            // Use actual seconds since last comm, or threshold+1 if never received
            // This prevents showing absurd values like "63905941014 seconds"
            double secondsValue = neverReceived
                ? _dtsCommunicationTimeoutSeconds + 1
                : timeSinceLastComm.TotalSeconds;

            string description = neverReceived
                ? "DTS communication never established"
                : $"DTS communication lost for {timeSinceLastComm.TotalSeconds:F0} seconds";

            string details = neverReceived
                ? "Waiting for first DTS response"
                : $"Last communication: {_lastDtsCommunication:yyyy-MM-dd HH:mm:ss}";

            await RaiseFaultAsync(
                SystemHealthFaultType.DtsCommunicationLost,
                secondsValue,
                _dtsCommunicationTimeoutSeconds,
                description,
                details);
        }
        else
        {
            // DTS OK
            _currentHealth.DtsResponsive = true;
            _currentHealth.LastCheck = DateTime.UtcNow;

            if (_activeFaults.ContainsKey(SystemHealthFaultType.DtsCommunicationLost))
            {
                await ClearFaultAsync(SystemHealthFaultType.DtsCommunicationLost);
            }
        }

        RefreshHealthPayload();
    }



    /// <summary>
    /// Checks DTS module temperature against thresholds.
    /// </summary>
    private async Task CheckDtsModuleTemperatureAsync()
    {
        var temp = _currentHealth.ModuleTemperature;

        // Check high temperature
        if (temp > _dtsModuleTempHighThreshold)
        {
            await RaiseFaultAsync(
                SystemHealthFaultType.DtsTemperatureHigh,
                temp,
                _dtsModuleTempHighThreshold,
                $"DTS module temperature is too high: {temp:F1}°C",
                $"Threshold: {_dtsModuleTempHighThreshold}°C");
        }
        else if (_activeFaults.ContainsKey(SystemHealthFaultType.DtsTemperatureHigh))
        {
            await ClearFaultAsync(SystemHealthFaultType.DtsTemperatureHigh);
        }

        // Check low temperature
        if (temp < _dtsModuleTempLowThreshold && temp != 0)
        {
            await RaiseFaultAsync(
                SystemHealthFaultType.DtsTemperatureLow,
                temp,
                _dtsModuleTempLowThreshold,
                $"DTS module temperature is too low: {temp:F1}°C",
                $"Threshold: {_dtsModuleTempLowThreshold}°C");
        }
        else if (_activeFaults.ContainsKey(SystemHealthFaultType.DtsTemperatureLow))
        {
            await ClearFaultAsync(SystemHealthFaultType.DtsTemperatureLow);
        }
    }

    /// <summary>
    /// Checks CPU load against threshold using rolling average to avoid false faults from spikes.
    /// Uses cached PerformanceCounter for better performance.
    /// </summary>
    //private async Task CheckCpuLoadAsync()
    //{
    //    try
    //    {
    //        // Create PerformanceCounter only once and reuse
    //        _cpuCounter ??= new PerformanceCounter("Processor", "% Processor Time", "_Total");

    //        _cpuCounter.NextValue(); // First call always returns 0
    //        await Task.Delay(100); // Wait for accurate reading
    //        float cpuLoad = _cpuCounter.NextValue();

    //        // Store the current CPU usage value (for display)
    //        _currentHealth.CpuUsage = cpuLoad;

    //        // Add to rolling average queue
    //        _cpuReadings.Enqueue(cpuLoad);
    //        while (_cpuReadings.Count > ROLLING_AVERAGE_SAMPLES)
    //        {
    //            _cpuReadings.Dequeue();
    //        }

    //        // Calculate average for fault detection (avoids false faults from temporary spikes)
    //        double cpuAverage = _cpuReadings.Average();

    //        if (cpuAverage > _cpuLoadThreshold)
    //        {
    //            await RaiseFaultAsync(
    //                SystemHealthFaultType.CpuLoadHigh,
    //                cpuAverage,
    //                _cpuLoadThreshold,
    //                $"CPU load is high: {cpuAverage:F1}% (avg of {_cpuReadings.Count} samples)",
    //                $"Current: {cpuLoad:F1}%, Threshold: {_cpuLoadThreshold}%");
    //        }
    //        else if (_activeFaults.ContainsKey(SystemHealthFaultType.CpuLoadHigh))
    //        {
    //            await ClearFaultAsync(SystemHealthFaultType.CpuLoadHigh);
    //        }
    //    }
    //    catch (Exception ex)
    //    {
    //        _logger.LogWarning(ex, "Unable to read CPU load");
    //    }
    //}
    private async Task CheckCpuLoadAsync()
    {
        try
        {
            float cpuLoad = 0f;

#if WINDOWS
            _cpuCounter ??= new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _cpuCounter.NextValue();
            await Task.Delay(100);
            cpuLoad = _cpuCounter.NextValue();
#else
            var firstLine = System.IO.File.ReadLines("/proc/stat").First();
            var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            long user = long.Parse(parts[1]);
            long nice = long.Parse(parts[2]);
            long system = long.Parse(parts[3]);
            long idle = long.Parse(parts[4]);
            long iowait = parts.Length > 5 ? long.Parse(parts[5]) : 0;

            long total = user + nice + system + idle + iowait;
            long idleTotal = idle + iowait;

            if (_prevTotalTime == 0)
            {
                _prevTotalTime = total;
                _prevIdleTime = idleTotal;
                return;
            }

            long totalDiff = total - _prevTotalTime;
            long idleDiff = idleTotal - _prevIdleTime;

            cpuLoad = totalDiff > 0
                ? (1f - (float)idleDiff / totalDiff) * 100f
                : 0f;

            _prevTotalTime = total;
            _prevIdleTime = idleTotal;
#endif

            _currentHealth.CpuUsage = cpuLoad;
            _cpuReadings.Enqueue(cpuLoad);
            while (_cpuReadings.Count > ROLLING_AVERAGE_SAMPLES)
                _cpuReadings.Dequeue();

            double cpuAverage = _cpuReadings.Average();

            if (cpuAverage > _cpuLoadThreshold)
            {
                await RaiseFaultAsync(
                    SystemHealthFaultType.CpuLoadHigh,
                    cpuAverage,
                    _cpuLoadThreshold,
                    $"CPU load is high: {cpuAverage:F1}% (avg)",
                    $"Current: {cpuLoad:F1}%");
            }
            else if (_activeFaults.ContainsKey(SystemHealthFaultType.CpuLoadHigh))
            {
                await ClearFaultAsync(SystemHealthFaultType.CpuLoadHigh);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to read CPU load");
        }
    }

    /// <summary>
    /// Checks system-wide memory usage against threshold using rolling average.
    /// Uses GC.GetGCMemoryInfo to get total available memory and calculates used memory.
    /// </summary>
    private async Task CheckMemoryLoadAsync()
    {
        try
        {
            double totalMemoryBytes = 0;
            double availableMemoryBytes = 0;

#if WINDOWS
            // Windows: use PerformanceCounter + GC info
            var gcInfo = GC.GetGCMemoryInfo();
            totalMemoryBytes = gcInfo.TotalAvailableMemoryBytes;

            try
            {
                using var availableMemCounter =
                    new PerformanceCounter("Memory", "Available Bytes");
                availableMemoryBytes = availableMemCounter.NextValue();
            }
            catch
            {
                // Safe fallback if counter fails
                availableMemoryBytes = totalMemoryBytes * 0.3;
            }
#else
            // Linux: read from /proc/meminfo
            var memInfo = System.IO.File.ReadAllLines("/proc/meminfo");

            long memTotalKb = 0;
            long memAvailableKb = 0;

            foreach (var line in memInfo)
            {
                if (line.StartsWith("MemTotal:"))
                    memTotalKb = long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);

                else if (line.StartsWith("MemAvailable:"))
                    memAvailableKb = long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
            }

            // Convert KB → Bytes
            totalMemoryBytes = memTotalKb * 1024.0;
            availableMemoryBytes = memAvailableKb * 1024.0;
#endif

            if (totalMemoryBytes <= 0)
                return;

            var usedMemoryBytes = totalMemoryBytes - availableMemoryBytes;
            var memoryLoadPercent = (usedMemoryBytes / totalMemoryBytes) * 100;

            // Clamp values
            if (memoryLoadPercent < 0) memoryLoadPercent = 0;
            if (memoryLoadPercent > 100) memoryLoadPercent = 100;

            // Store for UI / payload
            _currentHealth.MemoryUsage = memoryLoadPercent;
            _currentHealth.MemoryUsedGB = usedMemoryBytes / 1024 / 1024 / 1024;
            _currentHealth.MemoryTotalGB = totalMemoryBytes / 1024 / 1024 / 1024;

            // Rolling average (fault stability)
            _memoryReadings.Enqueue(memoryLoadPercent);
            while (_memoryReadings.Count > ROLLING_AVERAGE_SAMPLES)
            {
                _memoryReadings.Dequeue();
            }

            double memoryAverage = _memoryReadings.Average();

            if (memoryAverage > _memoryLoadThreshold)
            {
                await RaiseFaultAsync(
                    SystemHealthFaultType.MemoryLoadHigh,
                    memoryAverage,
                    _memoryLoadThreshold,
                    $"System memory load is high: {memoryAverage:F1}% (avg of {_memoryReadings.Count} samples)",
                    $"Current: {memoryLoadPercent:F1}%, Used: {_currentHealth.MemoryUsedGB:F1} GB / {_currentHealth.MemoryTotalGB:F1} GB");
            }
            else if (_activeFaults.ContainsKey(SystemHealthFaultType.MemoryLoadHigh))
            {
                await ClearFaultAsync(SystemHealthFaultType.MemoryLoadHigh);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to read memory load");
        }
    }

    /// <summary>
    /// Refreshes the CurrentHealthPayload JSON with all current values.
    /// </summary>
    private void RefreshHealthPayload()
    {
        CurrentHealthPayload = JsonSerializer.SerializeToElement(new
        {
            LampStatus = _currentHealth.LampStatus,
            DtsResponsive = _currentHealth.DtsResponsive,
            ModuleTemperature = _currentHealth.ModuleTemperature,
            ApdValue = _currentHealth.ApdValue,
            LaserCurrent = _currentHealth.LaserCurrent,
            PumpCurrent = _currentHealth.PumpCurrent,
            LastCheck = _currentHealth.LastCheck,
            CpuUsage = _currentHealth.CpuUsage,
            MemoryUsage = _currentHealth.MemoryUsage,
            MemoryUsedGB = _currentHealth.MemoryUsedGB,
            MemoryTotalGB = _currentHealth.MemoryTotalGB
        });
    }

    /// <summary>
    /// Raises a system health fault.
    /// </summary>
    private async Task RaiseFaultAsync(
        SystemHealthFaultType faultType,
        double currentValue,
        double thresholdValue,
        string description,
        string? details = null)
    {
        var now = DateTime.UtcNow;

        // Check if fault already exists
        if (_activeFaults.TryGetValue(faultType, out var existingFault))
        {
            existingFault.LastUpdatedAt = now;
            existingFault.CurrentValue = currentValue;

            // 🔔 ADD THIS so FAULT LED can re-evaluate
            await _eventAggregator.PublishAsync(
                new SystemHealthFaultEvent(existingFault, isNewFault: false, now));

            return;
        }


        // Create new fault
        var fault = new SystemHealthFault
        {
            FaultId = Interlocked.Increment(ref _nextFaultId),
            FaultType = faultType,
            IsActive = true,
            FirstDetectedAt = now,
            LastUpdatedAt = now,
            CurrentValue = currentValue,
            ThresholdValue = thresholdValue,
            Description = description,
            Details = details
        };

        if (_activeFaults.TryAdd(faultType, fault))
        {
            _logger.LogWarning(
                "System health fault detected: {FaultType} - {Description}",
                faultType, description);

            // Log to file
            await _faultLogger.LogHealthFaultAsync(fault, isNewFault: true, now);

            // Publish event
            await _eventAggregator.PublishAsync(
                new SystemHealthFaultEvent(fault, isNewFault: true, now));
            await _eventHistoryService.LogSystemFaultAsync(
                fault.FaultType.ToString(),
                true,
                fault.CurrentValue,
                fault.ThresholdValue,
                fault.Description,
                now);
        }
    }
    public bool IsHealthBad()
    {
        return !_activeFaults.IsEmpty;
    }

    /// <summary>
    /// Clears a system health fault.
    /// </summary>
    private async Task ClearFaultAsync(SystemHealthFaultType faultType)
    {
        if (_activeFaults.TryRemove(faultType, out var fault))
        {
            var now = DateTime.UtcNow;
            fault.IsActive = false;
            fault.ClearedAt = now;
            fault.LastUpdatedAt = now;

            _logger.LogInformation(
                "System health fault cleared: {FaultType}",
                faultType);

            // Log to file
            await _faultLogger.LogHealthFaultAsync(fault, isNewFault: false, now);

            // Publish event
            await _eventAggregator.PublishAsync(
                new SystemHealthFaultEvent(fault, isNewFault: false, now));

            // Log to event history (alarm events)
            await _eventHistoryService.LogSystemFaultAsync(
                fault.FaultType.ToString(),
                false,
                fault.CurrentValue,
                fault.ThresholdValue,
                fault.Description,
                now);
        }
    }

    /// <summary>
    /// Gets all active system health faults.
    /// </summary>
    public Task<List<SystemHealthFault>> GetActiveHealthFaultsAsync()
    {
        return Task.FromResult(_activeFaults.Values.ToList());
    }

    /// <summary>
    /// Disposes resources including timer, semaphore, and performance counter.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Dispose timer
        _monitoringTimer?.Dispose();

        // Dispose semaphore
        _monitoringSemaphore?.Dispose();

        // Dispose performance counter
        _cpuCounter?.Dispose();

        _logger.LogInformation("SystemHealthManager disposed");
    }

}
