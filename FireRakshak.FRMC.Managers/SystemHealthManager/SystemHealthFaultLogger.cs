using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using FireRakshak.FRMC.Core.Enums;
using FireRakshak.FRMC.Core.Models;
using FireRakshak.FRMC.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace FireRakshak.FRMC.Managers.SystemHealthManager
{
    /// <summary>
    /// Logs system health faults to the EventHistory folder for auditing.
    /// </summary>
    public class SystemHealthFaultLogger
    {
        private readonly ILogger<SystemHealthFaultLogger> _logger;
        private readonly FileSystemPersistence _fileSystemPersistence;
        private const string EventHistoryFolderPath = "EventHistory";

        public SystemHealthFaultLogger(
            ILogger<SystemHealthFaultLogger> logger,
            FileSystemPersistence fileSystemPersistence)
        {
            _logger = logger;
            _fileSystemPersistence = fileSystemPersistence;
        }

        /// <summary>
        /// Logs a system health fault event to daily JSON file.
        /// </summary>
        public async Task LogHealthFaultAsync(SystemHealthFault fault, bool isNewFault, DateTime timestamp)
        {
            try
            {
                var eventRecord = new HealthFaultEventRecord
                {
                    Timestamp = timestamp,
                    FaultId = fault.FaultId,
                    FaultType = fault.FaultType.ToString(),
                    IsActive = fault.IsActive,
                    IsNewFault = isNewFault,
                    CurrentValue = fault.CurrentValue,
                    ThresholdValue = fault.ThresholdValue,
                    Description = fault.Description,
                    Details = fault.Details,
                    FirstDetectedAt = fault.FirstDetectedAt,
                    ClearedAt = fault.ClearedAt
                };

                // Generate daily file path
                string dateString = timestamp.ToString("yyyyMMdd");
                string fileName = $"health_faults_{dateString}.json";
                string fullPath = Path.Combine(EventHistoryFolderPath, fileName);

                // Read existing events or create new list
                List<HealthFaultEventRecord> events = new List<HealthFaultEventRecord>();

                if (await _fileSystemPersistence.FileExistsAsync(fullPath))
                {
                    var existingJson = await _fileSystemPersistence.ReadFileAsync(fullPath);
                    if (!string.IsNullOrEmpty(existingJson))
                    {
                        events = JsonSerializer.Deserialize<List<HealthFaultEventRecord>>(
                            existingJson,
                            JsonSerializerSettings.DefaultOptions) ?? new List<HealthFaultEventRecord>();
                    }
                }

                // Append new event
                events.Add(eventRecord);

                // Serialize and save
                var json = JsonSerializer.Serialize(events, JsonSerializerSettings.DefaultOptions);
                await _fileSystemPersistence.WriteFileAsync(fullPath, json);

                _logger.LogInformation(
                    "Health fault logged: Type={Type}, IsNew={IsNew}, Value={Value}, File={File}",
                    fault.FaultType, isNewFault, fault.CurrentValue, fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error logging health fault {FaultType}", fault.FaultType);
            }
        }

        /// <summary>
        /// Represents a single health fault event record in the history.
        /// </summary>
        public class HealthFaultEventRecord
        {
            public DateTime Timestamp { get; set; }
            public int FaultId { get; set; }
            public string FaultType { get; set; } = string.Empty;
            public bool IsActive { get; set; }
            public bool IsNewFault { get; set; }
            public double CurrentValue { get; set; }
            public double ThresholdValue { get; set; }
            public string Description { get; set; } = string.Empty;
            public string? Details { get; set; }
            public DateTime FirstDetectedAt { get; set; }
            public DateTime? ClearedAt { get; set; }
        }
    }
}
