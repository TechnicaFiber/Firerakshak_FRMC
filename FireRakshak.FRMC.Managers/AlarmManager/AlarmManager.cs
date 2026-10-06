using FireRakshak.FRMC.Core.Enums;
using FireRakshak.FRMC.Core.EventAggregator;
using FireRakshak.FRMC.Core.Events;
using FireRakshak.FRMC.Core.Models;
using FireRakshak.FRMC.Host;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FireRakshak.FRMC.Managers.AlarmManager;

/// <summary>
/// Manages the lifecycle of alarms within the system. It listens for alarm triggers,
/// maintains a list of active alarms, handles alarm clearing, and logs all alarm-related activities.
/// This class is central to the system's safety functions.
/// </summary>
public class AlarmManager : IAlarmManager
{
    private readonly IEventAggregator _eventAggregator;
    private readonly ILogger<AlarmManager> _logger;
    private readonly AlarmLogger _alarmLogger;
    private static int _nextAlarmId; // Used for generating unique, sequential alarm IDs.

    // Using ConcurrentDictionary for thread-safe, in-memory storage of active alarms and their configurations.
    // This is crucial because alarm events can be triggered from multiple threads.
    private readonly ConcurrentDictionary<int, ActiveAlarm> _activeAlarms = new();
    private readonly ConcurrentDictionary<int, AlarmConfiguration> _alarmConfigurations = new();
    private readonly WebSocketServer _wsServer;

    // OPTIMIZATION: Composite key dictionary for O(1) alarm lookup by (ChannelId, ZoneId, AlarmType)
    // This replaces linear FirstOrDefault searches which were O(n)
    private readonly ConcurrentDictionary<(int ChannelId, int ZoneId, AlarmType Type), int> _alarmLookup = new();

    // ADDED: Lock for atomic check-and-add operations on _activeAlarms
    private readonly object _alarmAddLock = new object();

    /// <summary>
    /// Initializes a new instance of the <see cref="AlarmManager"/> class.
    /// </summary>
    /// <param name="eventAggregator">The event aggregator for subscribing to and publishing events.</param>
    /// <param name="logger">The logger for recording operational information.</param>
    /// <param name="alarmLogger">The service responsible for persisting alarm events to a log.</param>
    public AlarmManager(IEventAggregator eventAggregator, ILogger<AlarmManager> logger, AlarmLogger alarmLogger, WebSocketServer wsServer)
    {
        _eventAggregator = eventAggregator;
        _logger = logger;
        _alarmLogger = alarmLogger;
        _wsServer = wsServer;

        // Subscribe to events that drive the alarm lifecycle.
        _eventAggregator.Subscribe<AlarmTriggeredEvent>(HandleAlarmTriggeredEvent);
        _eventAggregator.Subscribe<AlarmClearedEvent>(HandleAlarmClearedEvent);
        _eventAggregator.Subscribe<AlarmAutoClearedEvent>(HandleAlarmAutoClearedEvent);
        _eventAggregator.Subscribe<AlarmResetRequestedEvent>(HandleAlarmResetRequestedEvent);
    }

    /// <inheritdoc/>
    public Task<AlarmConfiguration> GetAlarmConfigurationAsync(int alarmId)
    {
        _logger.LogInformation("Getting configuration for alarm {AlarmId}", alarmId);
        // TODO: This should retrieve the configuration from a persistent store (e.g., a file or database)
        // if it's not already in the in-memory cache.
        _alarmConfigurations.TryGetValue(alarmId, out var config);
        // Returning a default configuration if not found, to prevent null reference issues.
        return Task.FromResult(config ?? new AlarmConfiguration(alarmId, 0, Core.Enums.AlarmType.MaxTemperature, 0, new Dictionary<string, double>()));
    }

    /// <inheritdoc/>
    public Task UpdateAlarmConfigurationAsync(AlarmConfiguration config)
    {
        _logger.LogInformation("Updating configuration for alarm {AlarmId}", config.AlarmId);
        _alarmConfigurations.AddOrUpdate(config.AlarmId, config, (key, oldVal) => config);

        // TODO: Implement the logic to save the updated configuration to a persistent store.
        // After saving, a ConfigurationChangedEvent should be published to notify other parts of the system.
        // await _eventAggregator.PublishAsync(new ConfigurationChangedEvent(...));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<List<ActiveAlarm>> GetActiveAlarmsAsync()
    {
        // FIX: Use lock to get consistent snapshot during concurrent add/remove operations
        List<ActiveAlarm> snapshot;
        lock (_alarmAddLock)
        {
            snapshot = _activeAlarms.Values.ToList();
        }
        _logger.LogDebug("Retrieved {Count} active alarms", snapshot.Count);
        return Task.FromResult(snapshot);
    }

    /// <inheritdoc/>
    public async Task ClearAlarmAsync(int alarmId)
    {
        _logger.LogInformation("Clearing alarm {AlarmId}", alarmId);

        // FIX: Use lock for consistency with other alarm operations
        ActiveAlarm? clearedAlarm = null;

        lock (_alarmAddLock)
        {
            if (_activeAlarms.TryRemove(alarmId, out clearedAlarm))
            {
                // OPTIMIZATION: Also remove from lookup dictionary
                var lookupKey = (clearedAlarm.ChannelId, clearedAlarm.ZoneId, clearedAlarm.Type);
                _alarmLookup.TryRemove(lookupKey, out _);
            }
        }

        if (clearedAlarm != null)
        {
            // Log the cleared alarm for auditing
            await _alarmLogger.LogAlarmClearedAsync(clearedAlarm);

            // Publish event internally
            await _eventAggregator.PublishAsync(
                new AlarmClearedEvent(alarmId, DateTime.UtcNow)
            );

            _logger.LogInformation("Alarm {AlarmId} cleared successfully", alarmId);
        }
        else
        {
            _logger.LogWarning("Attempted to clear alarm {AlarmId}, but it was not found.", alarmId);
        }
    }

    /// <summary>
    /// Clears all active alarms.
    /// Used for manual reset or Modbus reset command.
    /// </summary>
    public async Task ClearAllAlarmsAsync()
    {
        await _eventAggregator.PublishAsync(
            new ResetAllZoneAlarmsRequestedEvent(DateTime.UtcNow)
        );
        _logger.LogWarning("Clearing ALL active alarms...");

        // FIX: Use lock for atomic snapshot and clear
        List<ActiveAlarm> alarmsToRemove;
        lock (_alarmAddLock)
        {
            alarmsToRemove = _activeAlarms.Values.ToList();
            _activeAlarms.Clear();
            // OPTIMIZATION: Also clear the lookup dictionary
            _alarmLookup.Clear();
        }

        // Batch-log all cleared alarms in a single file write (instead of N sequential writes + N directory scans)
        await _alarmLogger.LogAlarmsClearedBatchAsync(alarmsToRemove);

        _logger.LogWarning("All alarms cleared: {Count} alarms removed", alarmsToRemove.Count);

        // Send single WebSocket notification (skip per-alarm alarm_cleared broadcasts)
        var payload = new
        {
            ClearedCount = alarmsToRemove.Count,
            Timestamp = DateTime.UtcNow,
            Message = $"All alarms cleared ({alarmsToRemove.Count} alarms)"
        };

        var wsMessage = new WebSocketMessage(
            "all_alarms_cleared",
            JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement
        );

        await _wsServer.BroadcastMessageAsync(wsMessage);
    }

    /// <summary>
    /// Handles the <see cref="AlarmTriggeredEvent"/>, creating and logging a new active alarm.
    /// </summary>
    private async Task HandleAlarmTriggeredEvent(AlarmTriggeredEvent @event)
    {
        _logger.LogWarning(
            "ALARM TRIGGERED at CH:{ChannelId}, Z:{ZoneId}, Type:{AlarmType}, Value:{Value}°C",
            @event.ChannelId, @event.ZoneId, @event.Type, @event.Value);

        var alarmPayload = new
        {
            ChannelId = @event.ChannelId,
            ZoneId = @event.ZoneId,
            AlarmType = @event.Type.ToString(),
            TriggerValue = @event.Value,
            Position = @event.Position,
            Timestamp = @event.Timestamp,
            Message = $"Alarm Triggered → Zone {@event.ZoneId}, {@event.Type}"
        };

        JsonElement payloadElement =
            JsonDocument.Parse(JsonSerializer.Serialize(alarmPayload)).RootElement;

        var wsMessage = new WebSocketMessage("alarm_triggered", payloadElement);

        await _wsServer.BroadcastMessageAsync(wsMessage);


        // FIXED: Use lock to make check-and-add atomic
        ActiveAlarm? alarmToLog = null;
        bool isNewAlarm = false;

        lock (_alarmAddLock)
        {
            // OPTIMIZATION: O(1) lookup using composite key instead of FirstOrDefault O(n)
            var lookupKey = (@event.ChannelId, @event.ZoneId, @event.Type);
            ActiveAlarm? existingAlarm = null;

            if (_alarmLookup.TryGetValue(lookupKey, out int existingAlarmId))
            {
                _activeAlarms.TryGetValue(existingAlarmId, out existingAlarm);
            }
            //
            if (existingAlarm != null)
            {
                // 🔥 ONLY update if alarm is still ACTIVE (not latched-normalized)
                bool isStillActive = existingAlarm.Type switch
                {
                    AlarmType.PreAlarm => existingAlarm.ZoneStateSnapshot.PreAlarm,
                    AlarmType.MaxTemperature => existingAlarm.ZoneStateSnapshot.MaxAlarm,
                    AlarmType.MinTemperature => existingAlarm.ZoneStateSnapshot.MinAlarm,
                    _ => true
                };

                if (isStillActive)
                {
                    // ✅ normal behavior (same alarm continuing)
                    existingAlarm.TriggerValue = @event.Value;
                    existingAlarm.TriggeredAt = @event.Timestamp;
                    existingAlarm.Position = @event.Position;

                    existingAlarm.ZoneStateSnapshot.AverageTemperature = @event.Value;
                    existingAlarm.ZoneStateSnapshot.MaxTemperature = @event.Value;
                    existingAlarm.ZoneStateSnapshot.MinTemperature = @event.Value;
                    existingAlarm.ZoneStateSnapshot.LastUpdate = @event.Timestamp;
                }
                else
                {
                    // 🔥 KEY FIX: do NOTHING → preserve latched values
                    _logger.LogInformation(
                        "Ignoring new trigger for latched alarm → AlarmId:{AlarmId}",
                        existingAlarm.AlarmId);
                }
                //
                //if (existingAlarm != null)
                //{
                //    // Update existing alarm
                //    existingAlarm.TriggerValue = @event.Value;
                //    existingAlarm.TriggeredAt = @event.Timestamp;
                //    existingAlarm.Position = @event.Position;

                //    // Update snapshot
                //    existingAlarm.ZoneStateSnapshot.AverageTemperature = @event.Value;
                //    existingAlarm.ZoneStateSnapshot.MaxTemperature = @event.Value;
                //    existingAlarm.ZoneStateSnapshot.MinTemperature = @event.Value;
                //    existingAlarm.ZoneStateSnapshot.LastUpdate = @event.Timestamp;

                _logger.LogDebug("Updated existing alarm {AlarmId} for Ch{Ch}/Z{Z}/{Type}",
                    existingAlarm.AlarmId, @event.ChannelId, @event.ZoneId, @event.Type);

                // Notify client asynchronously outside lock (will be done after lock)
                alarmToLog = existingAlarm;
                isNewAlarm = false;
            }
            else
            {
                // Create new alarm (inside lock - atomic)
                var snapshot = new ZoneState(
                    zoneId: @event.ZoneId,
                    name: $"Zone {@event.ZoneId}",
                    startPoint: 0,
                    endPoint: 0,
                    averageTemperature: @event.Value,
                    maxTemperature: @event.Value,
                    minTemperature: @event.Value,
                    preAlarm: @event.Type == AlarmType.PreAlarm,
                    maxAlarm: @event.Type == AlarmType.MaxTemperature,
                    minAlarm: @event.Type == AlarmType.MinTemperature,
                    lastUpdate: @event.Timestamp
                );

                var newAlarm = new ActiveAlarm(
                    Interlocked.Increment(ref _nextAlarmId),
                    @event.ChannelId,
                    @event.ZoneId,
                    @event.Type,
                    @event.Value,
                    @event.Timestamp,
                    snapshot,
                    @event.Position
                );

                // Add to dictionary (atomic with check above)
                if (_activeAlarms.TryAdd(newAlarm.AlarmId, newAlarm))
                {
                    // OPTIMIZATION: Add to lookup dictionary for O(1) future lookups
                    _alarmLookup[lookupKey] = newAlarm.AlarmId;

                    _logger.LogWarning("NEW ALARM {AlarmId}: {Type} on Ch{Ch}/Z{Z} ({ZoneName}), Value={Value}",
                        newAlarm.AlarmId, newAlarm.Type, newAlarm.ChannelId, newAlarm.ZoneId,
                        @event.ZoneId, @event.Value);

                    alarmToLog = newAlarm;
                    isNewAlarm = true;
                }
                else
                {
                    _logger.LogError("Failed to add alarm to dictionary (AlarmId={AlarmId})", newAlarm.AlarmId);
                    return;
                }
            }
        }

        // Do I/O operations outside lock to minimize lock duration
        if (alarmToLog != null)
        {
            if (isNewAlarm)
            {
                // Log to history (only for new alarms)
                await _alarmLogger.LogAlarmTriggeredAsync(alarmToLog);
            }
            else
            {
                // Notify client that existing alarm was updated
                var updateObj = new
                {
                    AlarmId = alarmToLog.AlarmId,
                    ChannelId = alarmToLog.ChannelId,
                    ZoneId = alarmToLog.ZoneId,
                    AlarmType = alarmToLog.Type.ToString(),
                    TriggerValue = alarmToLog.TriggerValue,
                    Position = alarmToLog.Position,
                    Timestamp = alarmToLog.TriggeredAt
                };

                JsonElement updatePayload =
                    JsonDocument.Parse(JsonSerializer.Serialize(updateObj)).RootElement;

                await _wsServer.BroadcastMessageAsync(new WebSocketMessage("alarm_updated", updatePayload));
            }
        }
    }

    /// <summary>
    /// Handles the <see cref="AlarmClearedEvent"/> for logging and notification purposes.
    /// </summary>
    private async Task HandleAlarmClearedEvent(AlarmClearedEvent @event)
    {
        _logger.LogInformation("Alarm Cleared → AlarmId {AlarmId}", @event.AlarmId);

        var payload = new
        {
            AlarmId = @event.AlarmId,
            Timestamp = @event.Timestamp,
            Message = $"Alarm Cleared → Alarm {@event.AlarmId}"
        };

        var wsMessage = new WebSocketMessage(
            "alarm_cleared",
            JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement
        );

        await _wsServer.BroadcastMessageAsync(wsMessage);
    }

    /// <summary>
    /// Handles the <see cref="AlarmAutoClearedEvent"/> when alarm conditions return to normal.
    /// Automatically removes the matching alarm from the active alarms list.
    /// </summary>
    private async Task HandleAlarmAutoClearedEvent(AlarmAutoClearedEvent @event)
    {
        _logger.LogInformation(
            "Alarm condition returned to normal → CH:{ChannelId}, Z:{ZoneId}, Type:{Type}",
            @event.ChannelId, @event.ZoneId, @event.Type);

        ActiveAlarm? alarm = null;

        lock (_alarmAddLock)
        {
            var lookupKey = (@event.ChannelId, @event.ZoneId, @event.Type);

            // CHANGED: Only find alarm, do NOT remove it
            if (_alarmLookup.TryGetValue(lookupKey, out int alarmId))
            {
                _activeAlarms.TryGetValue(alarmId, out alarm);

                if (alarm != null)
                {
                    // CHANGED: Mark alarm as normal but keep it active
                    alarm.ZoneStateSnapshot.PreAlarm = false;
                    alarm.ZoneStateSnapshot.MaxAlarm = false;
                    alarm.ZoneStateSnapshot.MinAlarm = false;

                    _logger.LogInformation(
                        "Alarm latched but condition normal → AlarmId:{AlarmId}",
                        alarm.AlarmId);
                }
            }
        }

        // Optional UI notification
        if (alarm != null)
        {
            var payload = new
            {
                AlarmId = alarm.AlarmId,
                ChannelId = alarm.ChannelId,
                ZoneId = alarm.ZoneId,
                AlarmType = alarm.Type.ToString(),
                Timestamp = @event.Timestamp,
                Message = $"Alarm Normalized (latched) → Zone {alarm.ZoneId}, {alarm.Type}",
                AutoCleared = true,
                Latched = true
            };

            var wsMessage = new WebSocketMessage(
                "alarm_normalized",
                JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement
            );

            await _wsServer.BroadcastMessageAsync(wsMessage);
        }
    }


    /// <inheritdoc/>
    public Task<List<ActiveAlarm>> GetActiveAlarmsForChannelAsync(int channelId, int? zoneId = null)
    {
        List<ActiveAlarm> result;
        lock (_alarmAddLock)
        {
            if (zoneId.HasValue)
            {
                result = _activeAlarms.Values
                    .Where(a => a.ChannelId == channelId && a.ZoneId == zoneId.Value)
                    .ToList();
            }
            else
            {
                result = _activeAlarms.Values
                    .Where(a => a.ChannelId == channelId)
                    .ToList();
            }
        }
        _logger.LogDebug("Retrieved {Count} active alarms for channel {ChannelId}, zone {ZoneId}",
            result.Count, channelId, zoneId?.ToString() ?? "all");
        return Task.FromResult(result);
    }

    /// <summary>
    /// Handles the <see cref="AlarmResetRequestedEvent"/> when an alarm reset is requested
    /// from external sources (Modbus, WebSocket, etc.).
    /// </summary>
    private async Task HandleAlarmResetRequestedEvent(AlarmResetRequestedEvent @event)
    {
        _logger.LogWarning(
            "Alarm reset requested from {Source} → ResetAll:{ResetAll}",
            @event.Source, @event.ResetAll);

        if (@event.ResetAll)
        {
            // Reset all alarms
            await ClearAllAlarmsAsync();
            _logger.LogInformation("All alarms reset via {Source}", @event.Source);
        }
        else if (@event.AlarmId.HasValue)
        {
            // Reset specific alarm
            await ClearAlarmAsync(@event.AlarmId.Value);
            _logger.LogInformation("Alarm {AlarmId} reset via {Source}", @event.AlarmId.Value, @event.Source);
        }
    }



}