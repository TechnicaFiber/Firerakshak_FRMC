using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FireRakshak.FRMC.Core.Enums;
using Microsoft.Extensions.Logging;

namespace FireRakshak.FRMC.Managers.SystemConfigurationManager;

/// <summary>
/// Logs all configuration changes with timestamp, user, and change details.
/// </summary>
public class AuditTrailService
{
    private readonly ILogger<AuditTrailService> _logger;
    private readonly string _auditLogDirectory = "./AuditLogs"; // TODO: Make configurable

    /// <summary>
    /// Initializes a new instance of the <see cref="AuditTrailService"/> class.
    /// </summary>
    /// <param name="logger">The logger for recording information.</param>
    public AuditTrailService(ILogger<AuditTrailService> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(_auditLogDirectory);
    }

    /// <summary>
    /// Logs a configuration change event.
    /// </summary>
    /// <param name="configType">The type of configuration that changed.</param>
    /// <param name="changedBy">The user or system component that initiated the change.</param>
    /// <param name="key">The specific key or identifier of the changed setting.</param>
    /// <param name="oldValue">The old value of the setting.</param>
    /// <param name="newValue">The new value of the setting.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task LogConfigurationChangeAsync(ConfigurationType configType, string changedBy, string key, object oldValue, object newValue)
    {
        var change = new ConfigurationChange(DateTime.UtcNow, changedBy, configType.ToString(), key, oldValue, newValue);
        var logFilePath = Path.Combine(_auditLogDirectory, $"audit_{DateTime.UtcNow:yyyyMMdd}.json");
        // Write as single-line JSON (no indentation) so each line is a complete JSON object
        var logEntry = JsonSerializer.Serialize(change, new JsonSerializerOptions { WriteIndented = false }) + Environment.NewLine;

        await File.AppendAllTextAsync(logFilePath, logEntry);
        _logger.LogInformation("Configuration change logged: Type={ConfigType}, Key={Key}, ChangedBy={ChangedBy}", configType, key, changedBy);
    }

    /// <summary>
    /// Logs multiple configuration changes in a single batch operation.
    /// Much faster than calling LogConfigurationChangeAsync multiple times for bulk operations.
    /// </summary>
    /// <param name="changes">List of changes to log.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task LogConfigurationChangesBatchAsync(List<(ConfigurationType configType, string changedBy, string key, object oldValue, object newValue)> changes)
    {
        if (changes == null || changes.Count == 0)
            return;

        var logFilePath = Path.Combine(_auditLogDirectory, $"audit_{DateTime.UtcNow:yyyyMMdd}.json");
        var timestamp = DateTime.UtcNow;
        var serializerOptions = new JsonSerializerOptions { WriteIndented = false };

        // Build all log entries in memory first
        var logEntries = new System.Text.StringBuilder();
        foreach (var (configType, changedBy, key, oldValue, newValue) in changes)
        {
            var change = new ConfigurationChange(timestamp, changedBy, configType.ToString(), key, oldValue, newValue);
            logEntries.Append(JsonSerializer.Serialize(change, serializerOptions));
            logEntries.Append(Environment.NewLine);
        }

        // Single file write operation for all entries
        await File.AppendAllTextAsync(logFilePath, logEntries.ToString());

        _logger.LogInformation("Batch configuration changes logged: {Count} changes, Type={ConfigType}, ChangedBy={ChangedBy}",
            changes.Count, changes[0].configType, changes[0].changedBy);
    }

    /// <summary>
    /// Logs zone additions as a single summary entry.
    /// </summary>
    public async Task LogZonesAddedAsync(string changedBy, int channelId, int zonesAdded)
    {
        if (zonesAdded <= 0) return;

        var key = $"Channel {channelId}";
        var change = new ConfigurationChange(
            DateTime.UtcNow,
            changedBy,
            ConfigurationType.Zone.ToString(),
            key,
            "Zones Added",
            $"{zonesAdded} zone(s) added"
        );

        var logFilePath = Path.Combine(_auditLogDirectory, $"audit_{DateTime.UtcNow:yyyyMMdd}.json");
        var logEntry = JsonSerializer.Serialize(change, new JsonSerializerOptions { WriteIndented = false }) + Environment.NewLine;
        await File.AppendAllTextAsync(logFilePath, logEntry);

        _logger.LogInformation("Channel {ChannelId}: {ZonesAdded} zone(s) added, ChangedBy={ChangedBy}",
            channelId, zonesAdded, changedBy);
    }

    /// <summary>
    /// Logs a single zone deletion.
    /// </summary>
    public async Task LogZoneDeletedAsync(string changedBy, int channelId, int zoneId, string? zoneName = null)
    {
        var key = $"Channel {channelId}, Zone {zoneId}";
        var change = new ConfigurationChange(
            DateTime.UtcNow,
            changedBy,
            ConfigurationType.Zone.ToString(),
            key,
            zoneName ?? $"Zone {zoneId}",
            "Deleted"
        );

        var logFilePath = Path.Combine(_auditLogDirectory, $"audit_{DateTime.UtcNow:yyyyMMdd}.json");
        var logEntry = JsonSerializer.Serialize(change, new JsonSerializerOptions { WriteIndented = false }) + Environment.NewLine;
        await File.AppendAllTextAsync(logFilePath, logEntry);

        _logger.LogInformation("Zone deleted: Channel {ChannelId}, Zone {ZoneId}, ChangedBy={ChangedBy}",
            channelId, zoneId, changedBy);
    }

    /// <summary>
    /// Logs zone configuration changes summary (for batch updates where zones are modified but not added/deleted).
    /// </summary>
    public async Task LogZonesModifiedAsync(string changedBy, int channelId, int zonesModified)
    {
        if (zonesModified <= 0) return;

        var key = $"Channel {channelId}";
        var change = new ConfigurationChange(
            DateTime.UtcNow,
            changedBy,
            ConfigurationType.Zone.ToString(),
            key,
            "Zones Modified",
            $"{zonesModified} zone(s) configuration updated"
        );

        var logFilePath = Path.Combine(_auditLogDirectory, $"audit_{DateTime.UtcNow:yyyyMMdd}.json");
        var logEntry = JsonSerializer.Serialize(change, new JsonSerializerOptions { WriteIndented = false }) + Environment.NewLine;
        await File.AppendAllTextAsync(logFilePath, logEntry);

        _logger.LogInformation("Channel {ChannelId}: {ZonesModified} zone(s) modified, ChangedBy={ChangedBy}",
            channelId, zonesModified, changedBy);
    }

    /// <summary>
    /// Retrieves audit trail entries within a specified time range.
    /// </summary>
    /// <param name="from">The start date and time.</param>
    /// <param name="to">The end date and time.</param>
    /// <param name="maxEntries">Maximum number of entries to return (default 1000).</param>
    /// <returns>A list of ConfigurationChange objects.</returns>
    public async Task<List<ConfigurationChange>> GetAuditTrailAsync(DateTime from, DateTime to, int maxEntries = 1000)
    {
        _logger.LogInformation("Retrieving audit trail from {From} to {To}, max entries: {MaxEntries}", from, to, maxEntries);

        var allChanges = new List<ConfigurationChange>();

        try
        {
            // Get all audit files in the date range
            var startDate = from.Date;
            var endDate = to.Date;

            for (var date = startDate; date <= endDate; date = date.AddDays(1))
            {
                var logFilePath = Path.Combine(_auditLogDirectory, $"audit_{date:yyyyMMdd}.json");

                if (!File.Exists(logFilePath))
                    continue;

                // Read the entire file
                var fileContent = await File.ReadAllTextAsync(logFilePath);

                if (string.IsNullOrWhiteSpace(fileContent))
                    continue;

                // Split by newlines and parse each JSON entry
                var lines = fileContent.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var line in lines)
                {
                    try
                    {
                        var change = JsonSerializer.Deserialize<ConfigurationChange>(line);

                        if (change != null && change.Timestamp >= from && change.Timestamp <= to)
                        {
                            allChanges.Add(change);
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "Failed to parse audit log entry: {Line}", line);
                    }
                }
            }

            // Sort by timestamp descending (newest first)
            allChanges = allChanges.OrderByDescending(c => c.Timestamp).ToList();

            // Limit the number of entries to prevent memory/performance issues
            if (allChanges.Count > maxEntries)
            {
                _logger.LogWarning("Audit trail has {TotalCount} entries, limiting to {MaxEntries} (newest entries)",
                    allChanges.Count, maxEntries);
                allChanges = allChanges.Take(maxEntries).ToList();
            }

            _logger.LogInformation("Retrieved {Count} audit trail entries", allChanges.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving audit trail");
        }

        return allChanges;
    }

    /// <summary>
    /// Logs a general activity or event.
    /// </summary>
    /// <param name="activityMessage">The message describing the activity.</param>
    /// <param name="changedBy">Optional: The user or system component that initiated the activity.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task LogActivityAsync(string activityMessage, string changedBy = "System")
    {
        var logEntry = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} - {changedBy} - {activityMessage}{Environment.NewLine}";
        var logFilePath = Path.Combine(_auditLogDirectory, $"activity_{DateTime.UtcNow:yyyyMMdd}.log"); // Using .log for simple activities

        await File.AppendAllTextAsync(logFilePath, logEntry);
        _logger.LogInformation("Activity logged: {ActivityMessage}", activityMessage);
    }
}
