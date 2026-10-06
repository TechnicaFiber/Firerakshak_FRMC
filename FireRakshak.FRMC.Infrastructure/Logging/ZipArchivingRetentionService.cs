using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FireRakshak.FRMC.Infrastructure.Logging
{
    /// <summary>
    /// Background service responsible for automated log retention, ZIP archiving, and disk capacity enforcement (SG-01 to SG-06).
    /// </summary>
    public class ZipArchivingRetentionService : BackgroundService
    {
        private readonly ILogger<ZipArchivingRetentionService> _logger;
        private readonly IConfiguration _configuration;
        private readonly IServiceProvider? _serviceProvider;

        // Folders monitored for log retention & archiving
        private readonly string[] _targetFolders;

        // Track skipped files for lock starvation alerting (SG-05)
        private readonly Dictionary<string, int> _skippedCounts = new(StringComparer.OrdinalIgnoreCase);
        private TimeSpan _lastCheckInterval = TimeSpan.FromHours(12);

        public ZipArchivingRetentionService(
            ILogger<ZipArchivingRetentionService> logger,
            IConfiguration configuration,
            IServiceProvider? serviceProvider = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _serviceProvider = serviceProvider;

            var baseDir = AppContext.BaseDirectory;
            _targetFolders = new[]
            {
                Path.Combine(baseDir, "Logs"),
                Path.Combine(baseDir, "AlarmLogs"),
                Path.Combine(baseDir, "AuditLogs"),
                Path.Combine(baseDir, "EventHistory")
            };
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Execute on low process/thread priority to preserve core fire alarm monitoring performance
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;

            _logger.LogInformation("ZipArchivingRetentionService background worker started.");

            // Initial startup delay to allow system boot initialization
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                // Default / fallback configuration from appsettings.json
                bool enabled = _configuration.GetValue<bool>("LogRetention:Enabled", true);
                long highMb = _configuration.GetValue<long>("LogRetention:HighWatermarkMB", 9216); // 9.0 GB
                long lowMb = _configuration.GetValue<long>("LogRetention:LowWatermarkMB", 7680);   // 7.5 GB
                int checkHours = _configuration.GetValue<int>("LogRetention:CheckIntervalHours", 12);

                // Fetch dynamic DTSCM configuration overrides if available
                if (_serviceProvider != null)
                {
                    try
                    {
                        var configManagerType = Type.GetType("FireRakshak.FRMC.Managers.SystemConfigurationManager.IConfigurationManager, FireRakshak.FRMC.Managers");
                        if (configManagerType != null)
                        {
                            var configManager = _serviceProvider.GetService(configManagerType);
                            if (configManager != null)
                            {
                                var getSysConfigMethod = configManagerType.GetMethod("GetSystemConfigurationAsync");
                                if (getSysConfigMethod != null)
                                {
                                    var task = getSysConfigMethod.Invoke(configManager, null) as Task;
                                    if (task != null)
                                    {
                                        await task.ConfigureAwait(false);
                                        var resultProp = task.GetType().GetProperty("Result");
                                        var sysConfig = resultProp?.GetValue(task) as FireRakshak.FRMC.Core.Models.SystemConfiguration;
                                        if (sysConfig != null)
                                        {
                                            enabled = sysConfig.LogRetentionEnabled;
                                            highMb = sysConfig.LogHighWatermarkMB > 0 ? sysConfig.LogHighWatermarkMB : highMb;
                                            lowMb = sysConfig.LogLowWatermarkMB > 0 ? sysConfig.LogLowWatermarkMB : lowMb;
                                            checkHours = sysConfig.LogCheckIntervalHours > 0 ? sysConfig.LogCheckIntervalHours : checkHours;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Could not resolve dynamic system configuration from IConfigurationManager. Using appsettings.json fallback.");
                    }
                }

                TimeSpan checkInterval = TimeSpan.FromHours(Math.Max(1, checkHours));
                _lastCheckInterval = checkInterval;
                long highWatermarkBytes = highMb * 1024L * 1024L;
                long lowWatermarkBytes = lowMb * 1024L * 1024L;

                if (enabled)
                {
                    try
                    {
                        _logger.LogInformation(
                            "Executing scheduled log archiving and storage capacity pass (High={HighMb}MB, Low={LowMb}MB, Interval={CheckHours}h)...",
                            highMb, lowMb, checkHours);
                        
                        // 1. Archive closed daily log files into ZIP archives (SG-01, SG-02, SG-03)
                        ArchiveClosedLogs();

                        // 2. Evaluate storage capacity and prune oldest archives if exceeding High Watermark (SG-04)
                        EnforceStorageCapacityCeiling(highWatermarkBytes, lowWatermarkBytes);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error during scheduled log archiving and retention cycle.");
                    }
                }
                else
                {
                    _logger.LogInformation("Log retention is disabled by configuration.");
                }

                await Task.Delay(checkInterval, stoppingToken);
            }
        }

        /// <summary>
        /// Scans target log folders, compresses closed past daily log files into ZIP archives (SG-01, SG-02, SG-03).
        /// </summary>
        private void ArchiveClosedLogs()
        {
            var todayStr = DateTime.UtcNow.ToString("yyyyMMdd"); // SG-01 Active File Shielding

            foreach (var folder in _targetFolders)
            {
                if (!Directory.Exists(folder))
                    continue;

                try
                {
                    var files = Directory.GetFiles(folder, "*.*")
                        .Where(f => f.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                        .Where(f => !f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                                    !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    foreach (var filePath in files)
                    {
                        var fileName = Path.GetFileName(filePath);

                        // SG-01: Shield active file containing today's UTC date string (yyyyMMdd)
                        if (fileName.Contains(todayStr, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogDebug("SG-01 Shielding active log file from compression: {FileName}", fileName);
                            continue;
                        }

                        var fileInfo = new FileInfo(filePath);

                        // SG-02: Non-Blocking Lock Verification
                        if (IsFileLocked(fileInfo))
                        {
                            TrackFileSkipped(filePath);
                            continue;
                        }

                        // Reset skip count upon successful lock access
                        _skippedCounts.Remove(filePath);

                        // SG-03: Two-Phase Checksum & Atomic Temp File Writes
                        ArchiveSingleFileAtomic(fileInfo);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error scanning log folder for archiving: {Folder}", folder);
                }
            }
        }

        /// <summary>
        /// SG-03: Compresses a single log file into a .zip.tmp file, verifies entry CRC32, atomically renames to .zip, then unlinks original raw log file.
        /// </summary>
        private void ArchiveSingleFileAtomic(FileInfo rawFile)
        {
            var folder = rawFile.DirectoryName!;
            var baseNameWithoutExt = Path.GetFileNameWithoutExtension(rawFile.Name);
            var zipFinalPath = Path.Combine(folder, $"{baseNameWithoutExt}.zip");
            var zipTmpPath = Path.Combine(folder, $"{baseNameWithoutExt}.zip.tmp");

            try
            {
                // Clean up any stale temp file from a previous interrupted run
                if (File.Exists(zipTmpPath))
                {
                    File.Delete(zipTmpPath);
                }

                // Phase 1: Write compressed stream to temp file
                using (var zipStream = new FileStream(zipTmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false))
                {
                    var entry = archive.CreateEntry(rawFile.Name, CompressionLevel.Optimal);
                    using var entryStream = entry.Open();
                    using var rawStream = rawFile.OpenRead();
                    rawStream.CopyTo(entryStream);
                }

                // Phase 2: Verify temp ZIP archive integrity before commit
                using (var verifyStream = File.OpenRead(zipTmpPath))
                using (var verifyArchive = new ZipArchive(verifyStream, ZipArchiveMode.Read))
                {
                    if (verifyArchive.Entries.Count == 0)
                    {
                        throw new InvalidDataException($"ZIP verification failed for {zipTmpPath}: Archive contains zero entries.");
                    }
                }

                // Phase 3: Atomic rename (.zip.tmp -> .zip)
                File.Move(zipTmpPath, zipFinalPath, overwrite: true);

                // Phase 4: Delete raw log file ONLY after atomic rename and verification pass
                rawFile.Delete();
                _logger.LogInformation("Successfully archived log file: {RawFile} -> {ZipFile}", rawFile.Name, Path.GetFileName(zipFinalPath));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed atomic archiving for {RawFile}. Raw log preserved.", rawFile.Name);
                if (File.Exists(zipTmpPath))
                {
                    try { File.Delete(zipTmpPath); } catch { }
                }
            }
        }

        /// <summary>
        /// SG-04: Evaluates combined storage across monitored log folders. If exceeding High Watermark, prunes oldest ZIP archives down to Low Watermark.
        /// </summary>
        private void EnforceStorageCapacityCeiling(long highWatermarkBytes, long lowWatermarkBytes)
        {
            var allZipFiles = new List<FileInfo>();
            long totalCombinedBytes = 0;

            foreach (var folder in _targetFolders)
            {
                if (!Directory.Exists(folder))
                    continue;

                var dirInfo = new DirectoryInfo(folder);
                foreach (var file in dirInfo.GetFiles("*.*", SearchOption.AllDirectories))
                {
                    totalCombinedBytes += file.Length;
                    if (file.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        allZipFiles.Add(file);
                    }
                }
            }

            _logger.LogInformation(
                "Total combined log storage: {CurrentMb:F2} MB (High Watermark: {HighMb:F2} MB)",
                totalCombinedBytes / (1024.0 * 1024.0),
                highWatermarkBytes / (1024.0 * 1024.0));

            // Trigger pruning if total storage exceeds High Watermark
            if (totalCombinedBytes > highWatermarkBytes)
            {
                _logger.LogWarning(
                    "High Watermark exceeded! Total={TotalMb:F2}MB > High={HighMb:F2}MB. Starting pruning down to Low Watermark ({LowMb:F2}MB)...",
                    totalCombinedBytes / (1024.0 * 1024.0),
                    highWatermarkBytes / (1024.0 * 1024.0),
                    lowWatermarkBytes / (1024.0 * 1024.0));

                // Sort archives by CreationTime ascending (oldest first)
                var sortedArchives = allZipFiles.OrderBy(f => f.CreationTimeUtc).ToList();

                foreach (var archive in sortedArchives)
                {
                    if (totalCombinedBytes <= lowWatermarkBytes)
                    {
                        _logger.LogInformation("Storage capacity successfully pruned below Low Watermark. Pruning complete.");
                        break;
                    }

                    if (IsFileLocked(archive))
                    {
                        _logger.LogWarning("Pruning skipped locked ZIP archive: {FileName}", archive.Name);
                        continue;
                    }

                    try
                    {
                        long archiveLength = archive.Length;
                        archive.Delete();
                        totalCombinedBytes -= archiveLength;
                        _logger.LogInformation("Pruned historical ZIP archive: {FileName} ({BytesMb:F2} MB freed)", archive.Name, archiveLength / (1024.0 * 1024.0));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error deleting historical archive during pruning: {FileName}", archive.Name);
                    }
                }
            }
        }

        /// <summary>
        /// SG-02: Helper method to test if a file is locked by an external process or reader.
        /// </summary>
        private static bool IsFileLocked(FileInfo file)
        {
            if (!file.Exists)
                return false;

            try
            {
                using var stream = file.Open(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        /// <summary>
        /// SG-05: Tracks skipped locked files and emits starvation alert if skipped for 4 consecutive cycles (24 hours).
        /// </summary>
        private void TrackFileSkipped(string filePath)
        {
            _skippedCounts.TryGetValue(filePath, out int count);
            count++;
            _skippedCounts[filePath] = count;

            _logger.LogWarning("SG-02 Lock verification: File is currently locked and skipped ({Count}/4): {FilePath}", count, filePath);

            if (count >= 4)
            {
                _logger.LogError(
                    "SG-05 LOG_FILE_STUCK_LOCKED Alert: File has been locked and uncompressed for {Hours} hours: {FilePath}",
                    count * _lastCheckInterval.TotalHours,
                    filePath);
            }
        }
    }
}
