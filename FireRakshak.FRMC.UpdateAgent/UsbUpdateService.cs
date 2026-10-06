using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace FireRakshak.FRMC.UpdateAgent
{
    public class UsbUpdateService : BackgroundService
    {
        private readonly ILogger<UsbUpdateService> _logger;
        private readonly UpdateOptions _options;
        private readonly string _agentVersion = "1.3.0";
        private readonly List<string> _sessionLogs = new();
        private readonly HashSet<string> _processedDrives = new();


        public UsbUpdateService(ILogger<UsbUpdateService> logger, IOptions<UpdateOptions> options)
        {
            _logger = logger;
            _options = options.Value;

            // Dynamically set filenames based on DeviceId
            if (!string.IsNullOrEmpty(_options.DeviceId))
            {
                _options.UpdateFileName = $"{_options.DeviceId}.zip";
                _options.ForceUpdateFileName = $"{_options.DeviceId}_f.zip";
            }
        }

        private void LogSession(string message, LogLevel level = LogLevel.Information)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string entry = $"[{timestamp}] [{level}] {message}";
            _sessionLogs.Add(entry);
            
            // Still log to system console/journal for the service itself
            switch (level)
            {
                case LogLevel.Information: _logger.LogInformation(message); break;
                case LogLevel.Warning: _logger.LogWarning(message); break;
                case LogLevel.Error: _logger.LogError(message); break;
                case LogLevel.Critical: _logger.LogCritical(message); break;
            }
        }

        private async Task LogCyberSecurityEventAsync(string? drivePath, string message, bool isAuthorized = true)
        {
            try
            {
                string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
                
                // 1. Always log to internal FIFO file
                await LogToInternalSecurityFileAsync(logEntry);

                // 2. Add to session logs for update_status.txt
                LogSession($"Security Event: {message}");

                // 3. Handle USB logging
                if (!string.IsNullOrEmpty(drivePath) && Directory.Exists(drivePath))
                {
                    string securityFilePath = Path.Combine(drivePath, "Cyber security.txt");
                    
                    if (isAuthorized)
                    {
                        string header = await GetJsonHeaderAsync("CyberSecurity") + Environment.NewLine + Environment.NewLine;
                        
                        // For authorized USB, copy all internal logs
                        if (File.Exists(_options.LocalSecurityLogPath))
                        {
                            await File.WriteAllTextAsync(securityFilePath, header);
                            using (var sourceStream = new FileStream(_options.LocalSecurityLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            using (var destStream = new FileStream(securityFilePath, FileMode.Append, FileAccess.Write))
                            {
                                await sourceStream.CopyToAsync(destStream);
                            }
                        }
                        else
                        {
                            if (!File.Exists(securityFilePath))
                            {
                                await File.WriteAllTextAsync(securityFilePath, header + logEntry + Environment.NewLine);
                            }
                            else
                            {
                                await File.AppendAllTextAsync(securityFilePath, logEntry + Environment.NewLine);
                            }
                        }
                    }
                    else
                    {
                        // For unauthorized USB, only log the failure message
                        string failureMsg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Update failed: Unauthorized pendrive detected (Access Denied){Environment.NewLine}";
                        await File.AppendAllTextAsync(securityFilePath, failureMsg);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to process cyber security event: {Message}", ex.Message);
            }
        }

        private async Task LogToInternalSecurityFileAsync(string entry)
        {
            try
            {
                var logPath = _options.LocalSecurityLogPath;
                var dir = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                // Append the entry directly to the file (much more efficient than reading/writing everything)
                await File.AppendAllTextAsync(logPath, entry + Environment.NewLine);

                // Check size and prune if it exceeds 200MB
                var fileInfo = new FileInfo(logPath);
                const long maxSize = 200L * 1024 * 1024; // 200 MB

                if (fileInfo.Length > maxSize)
                {
                    // Keep the last 150MB of the file to avoid pruning on every write
                    const long keepSize = 150L * 1024 * 1024;
                    var tempFile = logPath + ".tmp";

                    using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        if (fs.Length > keepSize)
                        {
                            fs.Seek(-keepSize, SeekOrigin.End);
                            
                            using (var reader = new StreamReader(fs))
                            {
                                // Skip the first partial line
                                await reader.ReadLineAsync();
                                
                                using (var writer = new StreamWriter(tempFile))
                                {
                                    string? line;
                                    while ((line = await reader.ReadLineAsync()) != null)
                                    {
                                        await writer.WriteLineAsync(line);
                                    }
                                }
                            }
                        }
                    }

                    if (File.Exists(tempFile))
                    {
                        File.Move(tempFile, logPath, overwrite: true);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write to internal security log");
            }
        }

        private async Task<string> GetJsonHeaderAsync(string type)
        {
            string version = await GetSoftwareVersionAsync(Path.Combine(_options.InstallPath, _options.ConfigFileName));
            // If version is 0.0.0 (file not found), we can use a default or the agent version
            if (version == "0.0.0") version = "1.4.2"; 

            var data = new
            {
                device_id = _options.DeviceId ?? "DEV00123",
                type = type,
                frmc_version = version,
                createdat = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
            };
            return JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        }

        private async Task DumpLogsAsync(string drivePath, bool isAuthorized = true)
        {
            try
            {
                string statusFilePath = Path.Combine(drivePath, "update_status.txt");
                _logger.LogInformation("Saving session logs to USB: {Path}", statusFilePath);
                
                StringBuilder sb = new StringBuilder();
                if (isAuthorized)
                {
                    sb.AppendLine(await GetJsonHeaderAsync("UpdateStatus"));
                    sb.AppendLine();
                }
                
                sb.AppendLine($"=== FRMC Update Progress Milestone Key (Report Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}) ===");
                sb.AppendLine("0%   - Update Started");
                sb.AppendLine("5%   - Copying package to IPC");
                sb.AppendLine("10%  - Validating update package");
                sb.AppendLine("15%  - Stopping service");
                sb.AppendLine("20%  - Creating backup");
                sb.AppendLine("25%  - Starting Extraction");
                sb.AppendLine("25%-80% - Extraction Progress (Calculated)");
                sb.AppendLine("85%  - Setting permissions");
                sb.AppendLine("90%  - Restarting service");
                sb.AppendLine("95%  - Running Built-In Tests (BIT)");
                sb.AppendLine("===========================================");
                sb.AppendLine();
                sb.AppendLine("=== SESSION PROGRESS LOGS ===");

                if (!isAuthorized)
                {
                    sb.AppendLine("[ACCESS DENIED] Detailed session logs are restricted to authorized devices.");
                    sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Unauthorized access attempt detected.");
                }
                else
                {
                    foreach (var log in _sessionLogs)
                    {
                        sb.AppendLine(log);
                    }
                }

                await File.WriteAllTextAsync(statusFilePath, sb.ToString());
                _logger.LogInformation("Successfully saved session logs to {Path}", statusFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to copy logs to USB: {Message}", ex.Message);
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("FRMC Firmware Update Agent v{Version} started", _agentVersion);
            _logger.LogInformation("Monitoring {MountRoot} for update file: {FileName}", _options.MountRoot, _options.UpdateFileName);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckForUpdatesAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during update check cycle");
                }

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        private async Task CheckForUpdatesAsync(CancellationToken stoppingToken)
        {
            if (!Directory.Exists(_options.MountRoot))
            {
                return;
            }

            var mountDirs = Directory.GetDirectories(_options.MountRoot).ToList();
            
            // Cleanup state: Remove drives from memory that are no longer physically present
            _processedDrives.RemoveWhere(drive => !mountDirs.Contains(drive));

            foreach (var drivePath in mountDirs)
            {
                // If we already processed this specific drive in this session, skip it
                if (_processedDrives.Contains(drivePath))
                {
                    continue;
                }

                _logger.LogDebug("Scanning mount directory: {Path}", drivePath);
                
                // Search for any zip files to check for Device ID matching
                List<string> allZipFiles = new();
                try
                {
                    allZipFiles = Directory.EnumerateFiles(drivePath, "*.zip", SearchOption.AllDirectories).ToList();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Failed to scan directory {Path}: {Message}", drivePath, ex.Message);
                    continue;
                }

                if (!allZipFiles.Any()) continue;

                // Identify matching files for this specific device
                var regularFiles = allZipFiles.Where(f => Path.GetFileName(f).Equals(_options.UpdateFileName, StringComparison.OrdinalIgnoreCase)).ToList();
                var forceFiles = allZipFiles.Where(f => Path.GetFileName(f).Equals(_options.ForceUpdateFileName, StringComparison.OrdinalIgnoreCase)).ToList();

                // Prioritize force update if both exist
                string? updateFilePath = forceFiles.FirstOrDefault() ?? regularFiles.FirstOrDefault();
                bool isForceUpdate = forceFiles.Any();
                string serialId = await GetUsbSerialIdAsync(drivePath);

                if (updateFilePath != null)
                {
                    // PERSISTENCE CHECK: If we restarted, check the USB for a terminal status to avoid loops
                    string statusFilePath = Path.Combine(drivePath, "update_status.txt");
                    if (File.Exists(statusFilePath))
                    {
                        string statusContent = await File.ReadAllTextAsync(statusFilePath);
                        string packageVersion = GetSoftwareVersionFromZip(updateFilePath, _options.ConfigFileName);
                        
                        bool hasTerminalStatus = statusContent.Contains("Update Successful") || 
                                               statusContent.Contains("Update Failed") || 
                                               statusContent.Contains("Rollback Successful") ||
                                               statusContent.Contains("Rollback Failed");

                        if (hasTerminalStatus && statusContent.Contains($"\"frmc_version\": \"{packageVersion}\""))
                        {
                            _logger.LogInformation("Update already attempted for version {Version} on this USB (Terminal status found). Skipping to avoid loop.", packageVersion);
                            _processedDrives.Add(drivePath);
                            continue;
                        }
                    }

                    _logger.LogInformation("Found matching update file at {FilePath} (Force: {IsForce}). USB Serial ID: {SerialId}", updateFilePath, isForceUpdate, serialId);

                    if (_options.AuthorizedUsbSerialIds != null && _options.AuthorizedUsbSerialIds.Any(id => id.Trim().Equals(serialId.Trim(), StringComparison.OrdinalIgnoreCase)))
                    {
                        _logger.LogInformation("Authorized USB detected. Proceeding with update.");
                        await LogCyberSecurityEventAsync(drivePath, $"Authorized USB detected (Serial ID: {serialId}, Device ID Match: OK, Force: {isForceUpdate})");
                        await PerformUpdateAsync(drivePath, updateFilePath, isForceUpdate, stoppingToken);
                        
                        _logger.LogInformation("Update cycle completed for {Path}. Please remove USB.", updateFilePath);
                        
                        // Mark this drive as processed so we don't loop
                        _processedDrives.Add(drivePath);
                    }
                    else
                    {
                        _logger.LogWarning("Unauthorized USB detected (Serial: {SerialId}). Update skipped for {Path}.", serialId, updateFilePath);
                        await LogCyberSecurityEventAsync(drivePath, $"Unauthorized USB detected (Serial ID: {serialId})", false);
                        await ReportProgressAsync(drivePath, "Error: Unauthorized USB Device (Update Refused)");
                        
                        // Mark as processed so we don't spam logs
                        _processedDrives.Add(drivePath);
                    }
                }
                else
                {
                    // Zips found but none match DeviceID
                    _logger.LogWarning("USB drive at {Path} contains zip files, but none match Device ID '{DeviceId}'.", drivePath, _options.DeviceId);
                    
                    if (_options.AuthorizedUsbSerialIds != null && _options.AuthorizedUsbSerialIds.Any(id => id.Trim().Equals(serialId.Trim(), StringComparison.OrdinalIgnoreCase)))
                    {
                        string foundFiles = string.Join(", ", allZipFiles.Select(Path.GetFileName));
                        await LogCyberSecurityEventAsync(drivePath, $"Authorized USB detected but Device ID Mismatch. Expected: {_options.UpdateFileName}, Found: {foundFiles}");
                        await ReportProgressAsync(drivePath, $"Error: Device ID Mismatch (Expected {_options.DeviceId})");
                    }
                    else
                    {
                        await LogCyberSecurityEventAsync(drivePath, $"Unauthorized USB detected and Device ID Mismatch (Serial: {serialId})", false);
                        await ReportProgressAsync(drivePath, "Error: Unauthorized Device & ID Mismatch");
                    }
                    
                    // Mark as processed so we don't spam logs
                    _processedDrives.Add(drivePath);
                }
            }
        }

        private async Task<string> GetUsbSerialIdAsync(string drivePath)
        {
            try
            {
                // Use double quotes around drivePath to handle spaces
                var findmntPsi = new ProcessStartInfo
                {
                    FileName = "findmnt",
                    Arguments = $"-no SOURCE \"{drivePath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var findmntProcess = Process.Start(findmntPsi);
                if (findmntProcess == null) return "UNKNOWN_PROCESS_FAIL";
                
                string deviceSource = (await findmntProcess.StandardOutput.ReadToEndAsync()).Trim();
                string findmntError = await findmntProcess.StandardError.ReadToEndAsync();
                await findmntProcess.WaitForExitAsync();

                if (string.IsNullOrEmpty(deviceSource)) 
                {
                    _logger.LogWarning("findmnt failed for {Path}. Error: {Error}", drivePath, findmntError);
                    return "DEVICE_NOT_FOUND";
                }

                // Get the SERIAL ID for the device. 
                // We use -no SERIAL and -d (nodeps) which helps in getting the serial 
                // of the parent device if deviceSource is a partition.
                var lsblkPsi = new ProcessStartInfo
                {
                    FileName = "lsblk",
                    Arguments = $"-no SERIAL -d \"{deviceSource}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var lsblkProcess = Process.Start(lsblkPsi);
                if (lsblkProcess == null) return "UNKNOWN_LSBLK_FAIL";
                
                string serialId = (await lsblkProcess.StandardOutput.ReadToEndAsync()).Trim();
                
                // Fallback: If still empty, try to get the serial of the parent disk manually
                if (string.IsNullOrEmpty(serialId))
                {
                    // If device is /dev/sdb1, this tries to get serial for /dev/sdb
                    string parentDevice = System.Text.RegularExpressions.Regex.Replace(deviceSource, @"\d+$", "");
                    if (parentDevice != deviceSource)
                    {
                        var lsblkParentPsi = new ProcessStartInfo
                        {
                            FileName = "lsblk",
                            Arguments = $"-no SERIAL -d \"{parentDevice}\"",
                            RedirectStandardOutput = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var lsblkParentProcess = Process.Start(lsblkParentPsi);
                        if (lsblkParentProcess != null)
                        {
                            serialId = (await lsblkParentProcess.StandardOutput.ReadToEndAsync()).Trim();
                        }
                    }
                }

                if (string.IsNullOrEmpty(serialId))
                {
                    return "SERIAL_NOT_FOUND";
                }

                return serialId;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to retrieve USB Serial ID for {Path}: {Message}", drivePath, ex.Message);
                return "ERROR_EXC";
            }
        }

        private async Task ReportProgressAsync(string drivePath, string status, int? percentage = null)
        {
            string message = percentage.HasValue ? $"{status} - {percentage}%" : status;
            LogSession($"Update Progress: {message}");
        }

        private async Task PerformUpdateAsync(string drivePath, string packagePath, bool isForceUpdate, CancellationToken stoppingToken)
        {
            string backupDir = string.Empty;
            string tempZipPath = Path.Combine(_options.TempPath, Path.GetFileName(packagePath));
            
            _sessionLogs.Clear();
            LogSession($"--- New Update Session Started (Force: {isForceUpdate}) ---");

            try
            {
                await ReportProgressAsync(drivePath, "Update Started", 0);

                // 1. Copy to IPC temp
                await ReportProgressAsync(drivePath, "Copying package to IPC", 5);
                Directory.CreateDirectory(_options.TempPath);
                File.Copy(packagePath, tempZipPath, true);

                // 2. Validation & Version Check
                await ReportProgressAsync(drivePath, "Validating update package", 10);
                
                if (isForceUpdate)
                {
                    _logger.LogInformation("Forced update detected. Skipping version verification.");
                    await LogCyberSecurityEventAsync(drivePath, "Forced update detected. Version verification skipped.");
                }
                else
                {
                    string currentVersion = await GetSoftwareVersionAsync(Path.Combine(_options.InstallPath, _options.ConfigFileName));
                    string newVersion = GetSoftwareVersionFromZip(tempZipPath, _options.ConfigFileName);

                    _logger.LogInformation("Version Check - Installed: {Current}, Package: {New}", currentVersion, newVersion);

                    if (!IsNewerVersion(currentVersion, newVersion))
                    {
                        string msg = $"Update rejected: Current version ({currentVersion}) is newer or equal to package version ({newVersion})";
                        _logger.LogWarning(msg);
                        await LogCyberSecurityEventAsync(drivePath, $"FRMC Health Status: {msg}");
                        await ReportProgressAsync(drivePath, msg);
                        return; // Skip update
                    }

                    _logger.LogInformation("Version verification successful ({New} > {Current}). Proceeding with update.", newVersion, currentVersion);
                    await LogCyberSecurityEventAsync(drivePath, $"Version verification successful: {newVersion} > {currentVersion}");
                }

                // 3. Stop Main Service
                await ReportProgressAsync(drivePath, "Stopping service", 15);
                await RunCommandAsync("systemctl", $"stop {_options.MainServiceName}");
                await LogCyberSecurityEventAsync(drivePath, "FRMC Health Status: FRMC stopped");

                // 4. Backup Current Version
                if (Directory.Exists(_options.InstallPath))
                {
                    // Clean up old backups to keep only the latest one
                    if (Directory.Exists(_options.BackupPath))
                    {
                        foreach (var oldDir in Directory.GetDirectories(_options.BackupPath))
                        {
                            try
                            {
                                _logger.LogInformation("Removing old backup: {Path}", oldDir);
                                Directory.Delete(oldDir, true);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning("Failed to remove old backup {Path}: {Message}", oldDir, ex.Message);
                            }
                        }
                    }

                    string timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                    backupDir = Path.Combine(_options.BackupPath, timestamp);
                    await ReportProgressAsync(drivePath, "Creating backup", 20);
                    
                    Directory.CreateDirectory(_options.BackupPath);
                    // Use -a to preserve attributes and ensure a clean recursive copy
                    await RunCommandAsync("cp", $"-ra {_options.InstallPath} {backupDir}", true);
                    
                    // CRITICAL: Verify backup directory is not empty before proceeding to delete
                    if (!Directory.Exists(backupDir) || !Directory.EnumerateFileSystemEntries(backupDir).Any())
                    {
                        throw new Exception($"Backup failed: Directory '{backupDir}' is empty or does not exist.");
                    }

                    _logger.LogInformation("Backup verified successfully at {Path}", backupDir);

                    // CLEANING STEP: Remove old files to prevent DLL conflicts
                    _logger.LogInformation("Cleaning installation directory: {Path}", _options.InstallPath);
                    var dirInfo = new DirectoryInfo(_options.InstallPath);
                    foreach (var file in dirInfo.GetFiles()) file.Delete();
                    foreach (var dir in dirInfo.GetDirectories()) dir.Delete(true);
                }

                // 5. Extract Package with Progress
                await ReportProgressAsync(drivePath, "Extracting files", 25);
                Directory.CreateDirectory(_options.InstallPath);
                
                using (ZipArchive archive = ZipFile.OpenRead(tempZipPath))
                {
                    int totalFiles = archive.Entries.Count;
                    if (totalFiles == 0) throw new Exception("Update package is empty.");

                    int currentFile = 0;
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        string destinationPath = Path.GetFullPath(Path.Combine(_options.InstallPath, entry.FullName));
                        
                        if (!destinationPath.StartsWith(Path.GetFullPath(_options.InstallPath)))
                            continue;

                        if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                        {
                            Directory.CreateDirectory(destinationPath);
                        }
                        else
                        {
                            var parentDir = Path.GetDirectoryName(destinationPath);
                            if (parentDir != null) Directory.CreateDirectory(parentDir);
                            entry.ExtractToFile(destinationPath, true);
                        }
                        
                        currentFile++;
                        int progress = 25 + (currentFile * 55 / totalFiles); 
                        if (currentFile % 5 == 0 || currentFile == totalFiles)
                        {
                            await ReportProgressAsync(drivePath, "Extracting files", progress);
                        }
                    }
                }

                // 5b. Verify Application Executable Exists
                string hostExecPath = Path.Combine(_options.InstallPath, _options.HostExecutableName);
                if (!File.Exists(hostExecPath))
                {
                    var filesInDir = Directory.EnumerateFileSystemEntries(_options.InstallPath).Select(Path.GetFileName).ToList();
                    string foundList = string.Join(", ", filesInDir);
                    throw new Exception($"Critical failure: Application executable '{_options.HostExecutableName}' missing after extraction. Files found in install dir: {foundList}");
                }

                // 5c. Restore Configuration from Backup
                if (!string.IsNullOrEmpty(backupDir) && Directory.Exists(backupDir))
                {
                    // If backup was /opt/frmc/bin, backupDir is /opt/frmc/backup/ts
                    // But 'cp -a /opt/frmc/bin /opt/frmc/backup/ts' creates /opt/frmc/backup/ts/bin 
                    // if ts didn't exist? No, 'cp -a src dest' if dest doesn't exist makes dest a copy of src.
                    // So backupDir contains the CONTENTS of InstallPath.
                    
                    string oldConfigPath = Path.Combine(backupDir, "appsettings.json");
                    string newConfigPath = Path.Combine(_options.InstallPath, "appsettings.json");

                    if (File.Exists(oldConfigPath))
                    {
                        _logger.LogInformation("Restoring configuration from backup...");
                        try
                        {
                            if (File.Exists(newConfigPath))
                            {
                                var oldJsonStr = await File.ReadAllTextAsync(oldConfigPath);
                                var newJsonStr = await File.ReadAllTextAsync(newConfigPath);

                                var oldJson = JsonNode.Parse(oldJsonStr)?.AsObject();
                                var newJson = JsonNode.Parse(newJsonStr)?.AsObject();

                                if (oldJson != null && newJson != null)
                                {
                                    bool modified = false;
                                    if (oldJson.TryGetPropertyValue("Dts", out var dtsNode)) { newJson["Dts"] = dtsNode?.DeepClone(); modified = true; }
                                    if (oldJson.TryGetPropertyValue("RS232", out var rs232Node)) { newJson["RS232"] = rs232Node?.DeepClone(); modified = true; }

                                    if (modified)
                                    {
                                        await File.WriteAllTextAsync(newConfigPath, newJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                                        _logger.LogInformation("Selectively restored configuration.");
                                    }
                                }
                            }
                            else
                            {
                                File.Copy(oldConfigPath, newConfigPath, true);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to selectively restore configuration. Falling back to full copy.");
                            File.Copy(oldConfigPath, newConfigPath, true);
                        }
                    }
                }

                // 6. Apply Permissions
                await ReportProgressAsync(drivePath, "Setting permissions", 85);
                await ApplyPermissionsAsync();

                // 7. Restart Service
                await ReportProgressAsync(drivePath, "Restarting service", 90);
                await RunCommandAsync("systemctl", $"start {_options.MainServiceName}", true);
                await LogCyberSecurityEventAsync(drivePath, "FRMC Health Status: FRMC running");

                // 8. Run Built-In Tests (BIT)
                await ReportProgressAsync(drivePath, "Running Built-In Tests", 95);
                bool bitSuccess = await RunBuiltInTestsAsync(drivePath, stoppingToken);

                if (bitSuccess)
                {
                    await ReportProgressAsync(drivePath, "Update Successful");
                    string currentVersion = await GetSoftwareVersionAsync(Path.Combine(_options.InstallPath, _options.ConfigFileName));
                    await LogCyberSecurityEventAsync(drivePath, $"FRMC Health Status: Update done (Version: {currentVersion})");
                }
                else
                {
                    _logger.LogWarning("BIT FAILED for newly updated version. Triggering rollback...");
                    await LogCyberSecurityEventAsync(drivePath, "FRMC Health Status: Update failed (BIT Failure)");
                    await RollbackAsync(drivePath, backupDir, stoppingToken);
                }

                // Cleanup temp
                try { File.Delete(tempZipPath); } catch { }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Firmware update FAILED: {Message}", ex.Message);
                await ReportProgressAsync(drivePath, $"Update Failed: {ex.Message}");
                await LogCyberSecurityEventAsync(drivePath, $"FRMC Health Status: Update failed ({ex.Message})");
                
                if (!string.IsNullOrEmpty(backupDir) && Directory.Exists(backupDir))
                {
                    _logger.LogInformation("Attempting rollback due to exception during update...");
                    await RollbackAsync(drivePath, backupDir, stoppingToken);
                }
                else
                {
                    _logger.LogWarning("No backup available for rollback. Attempting to start service anyway.");
                    await RunCommandAsync("systemctl", $"start {_options.MainServiceName}");
                    await LogCyberSecurityEventAsync(drivePath, "FRMC Health Status: FRMC running (Emergency Restart)");
                }
            }
            finally
            {
                await DumpLogsAsync(drivePath);
            }
        }

        private async Task RollbackAsync(string drivePath, string backupDir, CancellationToken stoppingToken)
        {
            try
            {
                await ReportProgressAsync(drivePath, "Rollback Triggered", null);
                _logger.LogWarning("Rolling back to backup version: {BackupDir}", backupDir);

                if (string.IsNullOrEmpty(backupDir) || !Directory.Exists(backupDir))
                {
                    _logger.LogError("Rollback failed: Backup directory not found.");
                    await ReportProgressAsync(drivePath, "Rollback Failed: No Backup Available");
                    return;
                }

                // 1. Stop Service
                await RunCommandAsync("systemctl", $"stop {_options.MainServiceName}");
                string currentVersion = await GetSoftwareVersionAsync(Path.Combine(_options.InstallPath, _options.ConfigFileName));
                await LogCyberSecurityEventAsync(drivePath, $"FRMC Health Status: FRMC stopped (Rollback from Version: {currentVersion})");

                // 2. Clean Install Path
                _logger.LogInformation("Cleaning installation directory for rollback: {Path}", _options.InstallPath);
                if (Directory.Exists(_options.InstallPath))
                {
                    var dirInfo = new DirectoryInfo(_options.InstallPath);
                    foreach (var file in dirInfo.GetFiles()) file.Delete();
                    foreach (var dir in dirInfo.GetDirectories()) dir.Delete(true);
                }
                else
                {
                    Directory.CreateDirectory(_options.InstallPath);
                }

                // 3. Copy files from backup
                await ReportProgressAsync(drivePath, "Restoring from backup", 50);
                // Since 'cp -ra src dest' was used, backupDir contains the contents of InstallPath
                await RunCommandAsync("cp", $"-ra {backupDir}/. {_options.InstallPath}", true);

                // 4. Apply Permissions
                await ApplyPermissionsAsync();

                // 5. Restart Service
                await ReportProgressAsync(drivePath, "Restarting original service", 90);
                await RunCommandAsync("systemctl", $"start {_options.MainServiceName}", true);
                string restoredVersion = await GetSoftwareVersionAsync(Path.Combine(_options.InstallPath, _options.ConfigFileName));
                await LogCyberSecurityEventAsync(drivePath, $"FRMC Health Status: FRMC running (Rollback from {currentVersion} to {restoredVersion})");

                // 6. Verify Rollback
                bool rollbackBitSuccess = await RunBuiltInTestsAsync(drivePath, stoppingToken);
                if (rollbackBitSuccess)
                {
                    _logger.LogInformation("Rollback successful.");
                    await ReportProgressAsync(drivePath, "Rollback Successful");
                }
                else
                {
                    _logger.LogCritical("Rollback FAILED: Backup version is also failing BIT!");
                    await ReportProgressAsync(drivePath, "Critical Error: Rollback BIT Failure");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception during rollback");
                await ReportProgressAsync(drivePath, $"Rollback Error: {ex.Message}");
            }
        }

        private async Task ApplyPermissionsAsync()
        {
            _logger.LogInformation("Applying permissions to {Path}", _options.InstallPath);
            await RunCommandAsync("chmod", $"+x {_options.InstallPath}/{_options.HostExecutableName}", true);
            await RunCommandAsync("chown", $"-R {_options.AppUser}:{_options.AppUser} {_options.InstallPath}", true);
            await RunCommandAsync("setcap", $"cap_net_bind_service=+ep {_options.InstallPath}/{_options.HostExecutableName}", true);
        }

        private async Task<bool> RunBuiltInTestsAsync(string drivePath, CancellationToken stoppingToken)
        {
            _logger.LogInformation("Waiting for service to stabilize (15s)...");
            await Task.Delay(15000, stoppingToken);

            // Test 1: Service Active
            bool isActive = await CheckServiceActiveAsync(_options.MainServiceName);
            if (!isActive)
            {
                _logger.LogWarning("BIT: Service {ServiceName} is NOT active after restart", _options.MainServiceName);
                return false;
            }
            else
            {
                _logger.LogInformation("BIT: Service is active.");
                return true;
            }
        }

        private async Task<bool> CheckServiceActiveAsync(string serviceName)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "systemctl",
                    Arguments = $"is-active {serviceName}",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null) return false;
                
                string output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                
                return output.Trim() == "active";
            }
            catch { return false; }
        }

        private async Task RunCommandAsync(string command, string arguments, bool throwOnError = false)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null) throw new Exception($"Failed to start process: {command}");

                await process.WaitForExitAsync();
                
                if (process.ExitCode != 0)
                {
                    string error = await process.StandardError.ReadToEndAsync();
                    string msg = $"Command '{command} {arguments}' exited with code {process.ExitCode}: {error}";
                    _logger.LogWarning(msg);
                    if (throwOnError) throw new Exception(msg);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error running command {Command}", command);
                if (throwOnError) throw;
            }
        }

        private async Task<string> GetSoftwareVersionAsync(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return "0.0.0";
                var jsonContent = await File.ReadAllTextAsync(filePath);
                using var doc = JsonDocument.Parse(jsonContent);
                if (doc.RootElement.TryGetProperty("SoftwareVersion", out var versionProp))
                {
                    return versionProp.GetString() ?? "0.0.0";
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to read version from {Path}: {Message}", filePath, ex.Message);
            }
            return "0.0.0";
        }

        private string GetSoftwareVersionFromZip(string zipPath, string configFileName)
        {
            try
            {
                using ZipArchive archive = ZipFile.OpenRead(zipPath);
                var entry = archive.GetEntry(configFileName);
                if (entry != null)
                {
                    using var stream = entry.Open();
                    using var doc = JsonDocument.Parse(stream);
                    if (doc.RootElement.TryGetProperty("SoftwareVersion", out var versionProp))
                    {
                        return versionProp.GetString() ?? "0.0.0";
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to read version from ZIP {Path}: {Message}", zipPath, ex.Message);
            }
            return "0.0.0";
        }

        private bool IsNewerVersion(string currentVersion, string newVersion)
        {
            try
            {
                if (Version.TryParse(currentVersion, out var current) && Version.TryParse(newVersion, out var @new))
                {
                    return @new > current;
                }
            }
            catch { }
            return false;
        }
    }
}
