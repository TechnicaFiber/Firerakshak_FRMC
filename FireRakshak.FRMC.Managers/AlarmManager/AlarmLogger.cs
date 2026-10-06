using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FireRakshak.FRMC.Core.Models;
using Microsoft.Extensions.Logging;

namespace FireRakshak.FRMC.Managers.AlarmManager;

/// <summary>
/// Provides functionality to log alarm events to text files.
/// It handles the creation of daily log files, formats log messages,
/// and implements a retention policy to clean up old log files.
/// </summary>
public class AlarmLogger
{
    private readonly ILogger<AlarmLogger> _logger;
    // TODO: The log directory should be configurable via appsettings.json.
    private readonly string _logDirectory = Path.Combine(AppContext.BaseDirectory, "AlarmLogs");
    // TODO: The retention period should be configurable.
    private readonly TimeSpan _retentionPeriod = TimeSpan.FromDays(30);

    /// <summary>
    /// Initializes a new instance of the <see cref="AlarmLogger"/> class.
    /// </summary>
    /// <param name="logger">The logger for recording internal information and errors.</param>
    public AlarmLogger(ILogger<AlarmLogger> logger)
    {
        _logger = logger;
        // Ensure the log directory exists before any logging attempts.
        Directory.CreateDirectory(_logDirectory);
    }

    /// <summary>
    /// Asynchronously logs a triggered alarm event to the daily log file.
    /// </summary>
    /// <param name="alarm">The active alarm that was triggered.</param>
    /// <returns>A task that represents the asynchronous file write operation.</returns>
    public async Task LogAlarmTriggeredAsync(ActiveAlarm alarm)
    {
        var logFilePath = Path.Combine(_logDirectory, $"alarm_{alarm.TriggeredAt:yyyyMMdd}.log");
        var logEntry = $"[{alarm.TriggeredAt:yyyy-MM-dd HH:mm:ss}] ALARM TRIGGERED: ZoneId={alarm.ZoneId}, Type={alarm.Type}, Value={alarm.TriggerValue}{Environment.NewLine}";
        
        // Append the log entry to the file asynchronously to avoid blocking.
        await File.AppendAllTextAsync(logFilePath, logEntry);
        _logger.LogInformation("Alarm {AlarmId} logged as triggered.", alarm.AlarmId);
        
        // Log retention and cleanup are handled asynchronously by ZipArchivingRetentionService
    }

    /// <summary>
    /// Asynchronously logs a cleared alarm event to the daily log file.
    /// </summary>
    /// <param name="alarm">The active alarm that was cleared.</param>
    /// <returns>A task that represents the asynchronous file write operation.</returns>
    public async Task LogAlarmClearedAsync(ActiveAlarm alarm)
    {
        var logFilePath = Path.Combine(_logDirectory, $"alarm_{DateTime.UtcNow:yyyyMMdd}.log");
        var logEntry = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] ALARM CLEARED: AlarmId={alarm.AlarmId}, ZoneId={alarm.ZoneId}{Environment.NewLine}";
        
        await File.AppendAllTextAsync(logFilePath, logEntry);
        _logger.LogInformation("Alarm {AlarmId} logged as cleared.", alarm.AlarmId);

        CleanOldLogs();
    }

    /// <summary>
    /// Batch-logs multiple cleared alarms in a single file write, calling CleanOldLogs only once.
    /// </summary>
    public async Task LogAlarmsClearedBatchAsync(IReadOnlyList<ActiveAlarm> alarms)
    {
        if (alarms.Count == 0) return;

        var now = DateTime.UtcNow;
        var logFilePath = Path.Combine(_logDirectory, $"alarm_{now:yyyyMMdd}.log");

        var sb = new System.Text.StringBuilder(alarms.Count * 100);
        foreach (var alarm in alarms)
        {
            sb.Append($"[{now:yyyy-MM-dd HH:mm:ss}] ALARM CLEARED: AlarmId={alarm.AlarmId}, ZoneId={alarm.ZoneId}{Environment.NewLine}");
        }

        await File.AppendAllTextAsync(logFilePath, sb.ToString());
        _logger.LogInformation("Batch-logged {Count} cleared alarms.", alarms.Count);

        CleanOldLogs();
    }

    /// <summary>
    /// Scans the log directory and deletes any log files older than the configured retention period.
    /// </summary>
    private void CleanOldLogs()
    {
        _logger.LogDebug("Checking for and cleaning old alarm logs.");
        try
        {
            foreach (var file in Directory.EnumerateFiles(_logDirectory, "alarm_*.log"))
            {
                var creationTime = File.GetCreationTimeUtc(file);
                if (DateTime.UtcNow - creationTime > _retentionPeriod)
                {
                    try
                    {
                        File.Delete(file);
                        _logger.LogInformation("Deleted old alarm log file: {File}", file);
                    }
                    catch (Exception ex)
                    {
                        // Log an error if a specific file cannot be deleted, but continue the process.
                        _logger.LogError(ex, "Error deleting old alarm log file: {File}", file);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Log an error if the directory cannot be enumerated.
            _logger.LogError(ex, "Error enumerating alarm log files for cleanup.");
        }
    }
}
