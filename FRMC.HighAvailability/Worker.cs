using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;

namespace FRMC.HighAvailability
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;

        // Service and process names
        private const string FrmcServiceName = "FireRakshakFRMC";
        private const string FrmcProcessName = "FireRakshak.FRMC.Host";
        private const string FrmcExeName = "FireRakshak.FRMC.Host.exe";

        // Registry key where installer stores the install path
        private const string RegistryKeyPath = @"SOFTWARE\FireRakshak\FRMC";
        private const string InstallPathValueName = "InstallPath";

        // Default installation path (fallback)
        private const string DefaultInstallPath = @"C:\Program Files\Fire Rakshak\FRMC";

        // Restart check interval
        private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);

        // Delay before restart attempt (allow crash dumps, etc.)
        private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(5);

        // Maximum restart attempts before giving up (reset after successful run)
        private const int MaxRestartAttempts = 5;
        private int _restartAttempts = 0;
        private DateTime _lastSuccessfulCheck = DateTime.MinValue;

        private bool _restartInProgress = false;
        private string? _cachedFrmcPath = null;

        public Worker(ILogger<Worker> logger)
        {
            _logger = logger;
        }

        // ================= MAIN LOOP =================
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("FRMC High Availability Service started");
            _logger.LogInformation("Monitoring FRMC process and service for automatic recovery");

            // Initial delay to allow FRMC to start on boot
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await EnsureFrmcRunningAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "HA monitoring check failed");
                }

                await Task.Delay(CheckInterval, stoppingToken);
            }

            _logger.LogInformation("FRMC High Availability Service stopping");
        }

        // ================= CORE LOGIC =================
        private async Task EnsureFrmcRunningAsync(CancellationToken stoppingToken)
        {
            if (_restartInProgress)
                return;

            // Check if FRMC is running (either as service or process)
            bool isRunning = IsFrmcRunning();

            if (isRunning)
            {
                // Reset restart attempts on successful check
                if (_restartAttempts > 0)
                {
                    _logger.LogInformation("FRMC is running normally. Resetting restart counter.");
                    _restartAttempts = 0;
                }
                _lastSuccessfulCheck = DateTime.UtcNow;
                return;
            }

            // FRMC is not running - attempt restart
            _restartInProgress = true;

            try
            {
                _restartAttempts++;

                if (_restartAttempts > MaxRestartAttempts)
                {
                    _logger.LogCritical(
                        "FRMC has failed to start {Attempts} times. Manual intervention required.",
                        _restartAttempts);

                    // Reset after 5 minutes to allow retry
                    if ((DateTime.UtcNow - _lastSuccessfulCheck).TotalMinutes > 5)
                    {
                        _logger.LogWarning("Resetting restart counter after cooldown period");
                        _restartAttempts = 0;
                    }
                    return;
                }

                _logger.LogWarning(
                    "FRMC is not running. Attempting restart (attempt {Attempt}/{Max})...",
                    _restartAttempts, MaxRestartAttempts);

                // Wait before restart to allow any crash handling to complete
                await Task.Delay(RestartDelay, stoppingToken);

                // Try to start via Windows Service first
                if (await TryStartServiceAsync(stoppingToken))
                {
                    _logger.LogInformation("FRMC service started successfully via SCM");
                    return;
                }

                // Fallback: Start process directly
                if (StartFrmcProcess())
                {
                    _logger.LogInformation("FRMC process started successfully");
                    return;
                }

                _logger.LogError("Failed to start FRMC via service or process");
            }
            finally
            {
                _restartInProgress = false;
            }
        }

        private bool IsFrmcRunning()
        {
            // Method 1: Check if Windows Service is running
            try
            {
                using var sc = new ServiceController(FrmcServiceName);
                if (sc.Status == ServiceControllerStatus.Running)
                {
                    return true;
                }
            }
            catch (InvalidOperationException)
            {
                // Service not installed, check process directly
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error checking service status");
            }

            // Method 2: Check if process is running
            try
            {
                var processes = Process.GetProcessesByName(FrmcProcessName);
                if (processes.Length > 0)
                {
                    // Dispose process handles
                    foreach (var p in processes) p.Dispose();
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error checking process status");
            }

            return false;
        }

        private Task<bool> TryStartServiceAsync(CancellationToken stoppingToken)
        {
            try
            {
                using var sc = new ServiceController(FrmcServiceName);

                if (sc.Status == ServiceControllerStatus.Stopped ||
                    sc.Status == ServiceControllerStatus.StopPending)
                {
                    // Wait for stop to complete if pending
                    if (sc.Status == ServiceControllerStatus.StopPending)
                    {
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                    }

                    // Start the service
                    sc.Start();

                    // Wait for service to start (with timeout)
                    var timeout = TimeSpan.FromSeconds(60);
                    sc.WaitForStatus(ServiceControllerStatus.Running, timeout);

                    return Task.FromResult(sc.Status == ServiceControllerStatus.Running);
                }

                return Task.FromResult(false);
            }
            catch (InvalidOperationException)
            {
                _logger.LogDebug("FRMC service is not installed, will try direct process start");
                return Task.FromResult(false);
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                _logger.LogWarning("Timeout waiting for FRMC service to start");
                return Task.FromResult(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to start FRMC via Windows Service");
                return Task.FromResult(false);
            }
        }

        private bool StartFrmcProcess()
        {
            var frmcPath = GetFrmcExecutablePath();

            if (string.IsNullOrEmpty(frmcPath) || !File.Exists(frmcPath))
            {
                _logger.LogCritical(
                    "FRMC executable not found. Searched path: {Path}",
                    frmcPath ?? "NULL");
                return false;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = frmcPath,
                    WorkingDirectory = Path.GetDirectoryName(frmcPath)!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false
                };

                var process = Process.Start(startInfo);

                if (process != null)
                {
                    _logger.LogInformation(
                        "FRMC process started with PID {PID}",
                        process.Id);
                    process.Dispose();
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Failed to start FRMC process at {Path}", frmcPath);
                return false;
            }
        }

        private string GetFrmcExecutablePath()
        {
            // Return cached path if available and file exists
            if (!string.IsNullOrEmpty(_cachedFrmcPath) && File.Exists(_cachedFrmcPath))
            {
                return _cachedFrmcPath;
            }

            // Method 1: Read from registry (set by installer)
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(RegistryKeyPath);
                if (key != null)
                {
                    var installPath = key.GetValue(InstallPathValueName) as string;
                    if (!string.IsNullOrEmpty(installPath))
                    {
                        var exePath = Path.Combine(installPath, FrmcExeName);
                        if (File.Exists(exePath))
                        {
                            _cachedFrmcPath = exePath;
                            _logger.LogDebug("Found FRMC at registry path: {Path}", exePath);
                            return exePath;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read install path from registry");
            }

            // Method 2: Check default installation path
            var defaultPath = Path.Combine(DefaultInstallPath, FrmcExeName);
            if (File.Exists(defaultPath))
            {
                _cachedFrmcPath = defaultPath;
                _logger.LogDebug("Found FRMC at default path: {Path}", defaultPath);
                return defaultPath;
            }

            // Method 3: Check same directory as HA service
            var haDirectory = AppContext.BaseDirectory;
            var sameDirPath = Path.Combine(haDirectory, FrmcExeName);
            if (File.Exists(sameDirPath))
            {
                _cachedFrmcPath = sameDirPath;
                _logger.LogDebug("Found FRMC in same directory: {Path}", sameDirPath);
                return sameDirPath;
            }

            // Method 4: Check parent directory (if HA is in subdirectory)
            var parentDir = Directory.GetParent(haDirectory)?.FullName;
            if (!string.IsNullOrEmpty(parentDir))
            {
                var parentPath = Path.Combine(parentDir, FrmcExeName);
                if (File.Exists(parentPath))
                {
                    _cachedFrmcPath = parentPath;
                    _logger.LogDebug("Found FRMC in parent directory: {Path}", parentPath);
                    return parentPath;
                }
            }

            _logger.LogWarning("Could not locate FRMC executable in any known location");
            return defaultPath; // Return default path even if not found (for logging)
        }
    }
}
