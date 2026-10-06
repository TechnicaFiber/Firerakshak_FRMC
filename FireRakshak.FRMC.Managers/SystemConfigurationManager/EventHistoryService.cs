using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FireRakshak.FRMC.Core.Enums;
using FireRakshak.FRMC.Core.Models;
using FireRakshak.FRMC.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace FireRakshak.FRMC.Managers.SystemConfigurationManager
{
    /// <summary>
    /// Manages persistence of alarm events and zone states to the EventHistory folder.
    /// Only persists state when alarms are triggered or cleared.
    /// </summary>
    public class EventHistoryService
    {
        private readonly ILogger<EventHistoryService> _logger;
        private readonly FileSystemPersistence _fileSystemPersistence;
        private const string EventHistoryFolderPath = "EventHistory";

        public EventHistoryService(
            ILogger<EventHistoryService> logger,
            FileSystemPersistence fileSystemPersistence)
        {
            _logger = logger;
            _fileSystemPersistence = fileSystemPersistence;
        }

        /// <summary>
        /// Logs a system health fault event to the event history.
        /// </summary>
        public async Task LogSystemFaultAsync(
            string faultType,
            bool isActive,
            double? currentValue,
            double? thresholdValue,
            string description,
            DateTime timestamp)
        {
            try
            {
                var eventRecord = new AlarmEventRecord
                {
                    Timestamp = timestamp,
                    ChannelId = 0, // Indicates system-level event
                    ZoneId = 0,
                    ZoneName = "System",
                    AlarmType = $"SystemHealth:{faultType}",
                    IsActive = isActive,
                    TriggerValue = currentValue,
                    BreachPosition = thresholdValue,
                    ZoneStateSnapshot = new ZoneStateSnapshotRecord
                    {
                        AverageTemperature = 0,
                        MaxTemperature = 0,
                        MinTemperature = 0,
                        MaxTempPosition = 0,
                        RateOfRise = 0,
                        Deviation = 0,
                        StartPoint = 0,
                        EndPoint = 0,
                        ActiveAlarms = new List<string>()
                    }
                };

                // Generate daily file path
                string dateString = timestamp.ToString("yyyyMMdd");
                string fileName = $"alarm_events_{dateString}.json";
                string fullPath = Path.Combine(EventHistoryFolderPath, fileName);

                // Read existing events
                List<AlarmEventRecord> events = new List<AlarmEventRecord>();

                if (await _fileSystemPersistence.FileExistsAsync(fullPath))
                {
                    var existingJson = await _fileSystemPersistence.ReadFileAsync(fullPath);

                    if (!string.IsNullOrEmpty(existingJson))
                    {
                        events = JsonSerializer.Deserialize<List<AlarmEventRecord>>(
                            existingJson,
                            JsonSerializerSettings.DefaultOptions)
                            ?? new List<AlarmEventRecord>();
                    }
                }

                // Add description into ActiveAlarms so UI can show it
                eventRecord.ZoneStateSnapshot.ActiveAlarms.Add(description);

                // Append event
                events.Add(eventRecord);

                // Save file
                var json = JsonSerializer.Serialize(events, JsonSerializerSettings.DefaultOptions);

                await _fileSystemPersistence.WriteFileAsync(fullPath, json);

                _logger.LogInformation(
                    "System health fault logged: Type={Type}, Active={Active}, File={File}",
                    faultType,
                    isActive,
                    fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error logging system health fault {FaultType}",
                    faultType);
            }
        }

        /// <summary>
        /// Logs an alarm event with the zone state snapshot to a daily JSON file.
        /// </summary>
        public async Task LogAlarmEventAsync(
            int channelId,
            int zoneId,
            AlarmType alarmType,
            bool isActive,
            double? triggerValue,
            double? breachPosition,
            ZoneState zoneStateSnapshot,
            DateTime timestamp)
        {
            try
            {
                var eventRecord = new AlarmEventRecord
                {
                    Timestamp = timestamp,
                    ChannelId = channelId,
                    ZoneId = zoneId,
                    ZoneName = zoneStateSnapshot.Name,
                    AlarmType = alarmType.ToString(),
                    IsActive = isActive,
                    TriggerValue = triggerValue,
                    BreachPosition = breachPosition,
                    ZoneStateSnapshot = new ZoneStateSnapshotRecord
                    {
                        AverageTemperature = zoneStateSnapshot.AverageTemperature,
                        MaxTemperature = zoneStateSnapshot.MaxTemperature,
                        MinTemperature = zoneStateSnapshot.MinTemperature,
                        MaxTempPosition = zoneStateSnapshot.MaxTempPosition,
                        RateOfRise = zoneStateSnapshot.RateOfRise,
                        Deviation = zoneStateSnapshot.Deviation,
                        StartPoint = zoneStateSnapshot.StartPoint,
                        EndPoint = zoneStateSnapshot.EndPoint,
                        ActiveAlarms = new List<string>()
                    }
                };

                // Capture all active alarms
                if (zoneStateSnapshot.MaxAlarm) eventRecord.ZoneStateSnapshot.ActiveAlarms.Add("MaxTemperature");
                if (zoneStateSnapshot.MinAlarm) eventRecord.ZoneStateSnapshot.ActiveAlarms.Add("MinTemperature");
                if (zoneStateSnapshot.PreAlarm) eventRecord.ZoneStateSnapshot.ActiveAlarms.Add("PreAlarm");
                if (zoneStateSnapshot.RorAlarm) eventRecord.ZoneStateSnapshot.ActiveAlarms.Add("RateOfRise");
                if (zoneStateSnapshot.DeviationAlarm) eventRecord.ZoneStateSnapshot.ActiveAlarms.Add("Deviation");

                // Generate daily file path
                string dateString = timestamp.ToString("yyyyMMdd");
                string fileName = $"alarm_events_{dateString}.json";
                string fullPath = Path.Combine(EventHistoryFolderPath, fileName);

                // Read existing events or create new list
                List<AlarmEventRecord> events = new List<AlarmEventRecord>();

                if (await _fileSystemPersistence.FileExistsAsync(fullPath))
                {
                    var existingJson = await _fileSystemPersistence.ReadFileAsync(fullPath);
                    if (!string.IsNullOrEmpty(existingJson))
                    {
                        events = JsonSerializer.Deserialize<List<AlarmEventRecord>>(
                            existingJson,
                            JsonSerializerSettings.DefaultOptions) ?? new List<AlarmEventRecord>();
                    }
                }

                // Append new event
                events.Add(eventRecord);

                // Serialize and save
                var json = JsonSerializer.Serialize(events, JsonSerializerSettings.DefaultOptions);
                await _fileSystemPersistence.WriteFileAsync(fullPath, json);

                _logger.LogInformation(
                    "Alarm event logged: Ch={ChannelId}, Z={ZoneId}, Type={Type}, Active={Active}, File={File}",
                    channelId, zoneId, alarmType, isActive, fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error logging alarm event for Channel {ChannelId}, Zone {ZoneId}",
                    channelId, zoneId);
            }
        }

        /// <summary>
        /// Retrieves alarm event history within a specified time range.
        /// </summary>
        /// <param name="from">The start date and time.</param>
        /// <param name="to">The end date and time.</param>
        /// <param name="maxEntries">Maximum number of entries to return (default 1000).</param>
        /// <returns>A list of AlarmEventRecord objects.</returns>
        public async Task<List<AlarmEventRecord>> GetEventHistoryAsync(DateTime from, DateTime to, int maxEntries = 1000)
        {
            _logger.LogInformation("Retrieving event history from {From} to {To}, max entries: {MaxEntries}", from, to, maxEntries);

            var allEvents = new List<AlarmEventRecord>();

            try
            {
                // Get all event files in the date range
                var startDate = from.Date;
                var endDate = to.Date;

                for (var date = startDate; date <= endDate; date = date.AddDays(1))
                {
                    string dateString = date.ToString("yyyyMMdd");
                    string fileName = $"alarm_events_{dateString}.json";
                    string fullPath = Path.Combine(EventHistoryFolderPath, fileName);

                    if (!await _fileSystemPersistence.FileExistsAsync(fullPath))
                        continue;

                    var fileContent = await _fileSystemPersistence.ReadFileAsync(fullPath);

                    if (string.IsNullOrWhiteSpace(fileContent))
                        continue;

                    var events = JsonSerializer.Deserialize<List<AlarmEventRecord>>(
                        fileContent,
                        JsonSerializerSettings.DefaultOptions);

                    if (events != null)
                    {
                        // Filter by time range
                        var filteredEvents = events.Where(e => e.Timestamp >= from && e.Timestamp <= to).ToList();
                        allEvents.AddRange(filteredEvents);
                    }
                }

                // Sort by timestamp descending (newest first)
                allEvents = allEvents.OrderByDescending(e => e.Timestamp).ToList();

                // Limit the number of entries to prevent memory/performance issues
                if (allEvents.Count > maxEntries)
                {
                    _logger.LogWarning("Event history has {TotalCount} entries, limiting to {MaxEntries} (newest entries)",
                        allEvents.Count, maxEntries);
                    allEvents = allEvents.Take(maxEntries).ToList();
                }

                _logger.LogInformation("Retrieved {Count} event history entries", allEvents.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving event history from {From} to {To}", from, to);
            }

            return allEvents;
        }

        /// <summary>
        /// Represents a single alarm event record in the history.
        /// </summary>
        public class AlarmEventRecord
        {
            public DateTime Timestamp { get; set; }
            public int ChannelId { get; set; }
            public int ZoneId { get; set; }
            public string ZoneName { get; set; }
            public string AlarmType { get; set; }
            public bool IsActive { get; set; }
            public double? TriggerValue { get; set; }
            public double? BreachPosition { get; set; }
            public ZoneStateSnapshotRecord ZoneStateSnapshot { get; set; }
        }

        /// <summary>
        /// Snapshot of zone state at the time of the alarm.
        /// </summary>
        public class ZoneStateSnapshotRecord
        {
            public double AverageTemperature { get; set; }
            public double MaxTemperature { get; set; }
            public double MinTemperature { get; set; }
            public double MaxTempPosition { get; set; }
            public double RateOfRise { get; set; }
            public double Deviation { get; set; }
            public double StartPoint { get; set; }
            public double EndPoint { get; set; }
            public List<string> ActiveAlarms { get; set; }
        }
    }
}
