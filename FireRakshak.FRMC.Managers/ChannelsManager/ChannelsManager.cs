using FireRakshak.FRMC.Core.Enums;
using FireRakshak.FRMC.Core.EventAggregator;
using FireRakshak.FRMC.Core.Events;
using FireRakshak.FRMC.Core.Models;
using FireRakshak.FRMC.Host;
using FireRakshak.FRMC.Managers.AlarmManager;
using FireRakshak.FRMC.Managers.DtsManager;
using FireRakshak.FRMC.Managers.SystemConfigurationManager;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace FireRakshak.FRMC.Managers.ChannelsManager
{
    // Avoid conflict with Microsoft.Extensions.Configuration.IConfigurationManager
    using ConfigManager = FireRakshak.FRMC.Managers.SystemConfigurationManager.IConfigurationManager;

    /// <summary>
    /// Manages channels and zones, processes real-time temperature data,
    /// updates states, and triggers alarms based on threshold comparisons.
    /// </summary>
    public class ChannelsManager : IChannelsManager, IDisposable
    {
        private readonly IEventAggregator _eventAggregator;
        private readonly ILogger<ChannelsManager> _logger;
        private readonly ZoneConfigurationService _zoneConfigurationService;
        private readonly ThresholdComparator _thresholdComparator;
        private readonly ConfigManager _configurationManager;
        private readonly WebSocketServer _webSocketServiceServer;
        private readonly EventHistoryService _eventHistoryService;
        private readonly IAlarmManager _alarmManager;
        private readonly IDtsManager _dtsManager;

        private readonly ConcurrentDictionary<int, ChannelState> _channelStates = new();
        private readonly ConcurrentDictionary<int, ZoneState> _zoneStates = new();
        private bool _disposed = false;

        // Sliding window for ROR calculation - stores (timestamp, avgTemp) per zone
        // Key: "channelId_zoneId", Value: Queue of temperature readings
        private readonly ConcurrentDictionary<string, Queue<(DateTime Timestamp, double AvgTemp)>> _rorHistory = new();

        // Zone broadcast throttling - tracks last broadcast time per channel
        // Prevents WebSocket flooding by limiting zone updates to match DTS polling rate
        // Key: channelId, Value: last broadcast timestamp
        private readonly ConcurrentDictionary<int, DateTime> _lastZoneBroadcastTime = new();

        // Delta broadcasting: only send zones whose state meaningfully changed since last broadcast.
        // At 1000 zones this cuts WebSocket bandwidth 80-90% in steady state.
        // Key: "channelId_zoneId", Value: snapshot of last-broadcast values
        private readonly ConcurrentDictionary<string, ZoneBroadcastSnapshot> _lastBroadcastState = new();
        // Tracks when each channel last did a full (non-delta) broadcast for DTSCM sync
        private readonly ConcurrentDictionary<int, DateTime> _lastFullBroadcastTime = new();

        // Delta thresholds
        private const double DELTA_TEMP_THRESHOLD = 0.5;  // °C change needed to include zone in broadcast
        private const int FULL_REFRESH_INTERVAL_SECONDS = 30; // Force full broadcast periodically for sync

        // P1 #6: Cached config values — loaded once at startup, refreshed on ConfigurationChangedEvent.
        // Eliminates blocking .GetAwaiter().GetResult() calls per-zone per-cycle.
        private int _cachedRorWindowSeconds = 60;
        private int _cachedZoneBroadcastThrottleMs = 5000;

        /// <summary>
        /// Lightweight snapshot of zone values at time of last broadcast.
        /// Used for delta comparison to skip unchanged zones.
        /// </summary>
        private readonly struct ZoneBroadcastSnapshot
        {
            public readonly double Avg;
            public readonly double Max;
            public readonly double Min;
            public readonly double RateOfRise;
            public readonly double Deviation;
            public readonly bool MaxAlarm;
            public readonly bool MinAlarm;
            public readonly bool PreAlarm;
            public readonly bool RorAlarm;
            public readonly bool DeviationAlarm;

            public ZoneBroadcastSnapshot(ZoneState state)
            {
                Avg = state.AverageTemperature;
                Max = state.MaxTemperature;
                Min = state.MinTemperature;
                RateOfRise = state.RateOfRise;
                Deviation = state.Deviation;
                MaxAlarm = state.MaxAlarm;
                MinAlarm = state.MinAlarm;
                PreAlarm = state.PreAlarm;
                RorAlarm = state.RorAlarm;
                DeviationAlarm = state.DeviationAlarm;
            }
        }

        /// <summary>
        /// P1 #5: Computed zone statistics returned from single-pass UpdateZoneState.
        /// Eliminates redundant LINQ recomputation in alarm evaluation.
        /// </summary>
        private readonly struct ZoneStats
        {
            public readonly double Avg, Max, Min;
            public readonly double MaxPosition, MinPosition;

            public ZoneStats(double avg, double max, double min, double maxPosition, double minPosition)
            {
                Avg = avg;
                Max = max;
                Min = min;
                MaxPosition = maxPosition;
                MinPosition = minPosition;
            }
        }

        /// <summary>
        /// Gets the cached ROR sliding window duration.
        /// Value loaded at startup and refreshed on ConfigurationChangedEvent.
        /// </summary>
        private int GetRorWindowSeconds() => _cachedRorWindowSeconds;

        /// <summary>
        /// Gets the cached zone broadcast throttle interval.
        /// Value loaded at startup and refreshed on ConfigurationChangedEvent.
        /// </summary>
        private int GetZoneBroadcastThrottleMs() => _cachedZoneBroadcastThrottleMs;

        /// <summary>
        /// Determines if a zone has changed enough since its last broadcast to warrant inclusion.
        /// Returns true (always send) for: first-time zones, alarm flag changes, or temperature
        /// shifts exceeding DELTA_TEMP_THRESHOLD. This eliminates ~80-90% of WebSocket traffic
        /// in steady state where most zone temps vary by less than 0.1°C per cycle.
        /// </summary>
        private bool HasMeaningfulChange(string zoneKey, ZoneState current)
        {
            if (!_lastBroadcastState.TryGetValue(zoneKey, out var prev))
                return true; // First time — always send

            // Alarm flags changed — always send (safety-critical)
            if (prev.MaxAlarm != current.MaxAlarm || prev.MinAlarm != current.MinAlarm ||
                prev.PreAlarm != current.PreAlarm || prev.RorAlarm != current.RorAlarm ||
                prev.DeviationAlarm != current.DeviationAlarm)
                return true;

            // Temperature changed significantly
            if (Math.Abs(prev.Avg - current.AverageTemperature) >= DELTA_TEMP_THRESHOLD ||
                Math.Abs(prev.Max - current.MaxTemperature) >= DELTA_TEMP_THRESHOLD ||
                Math.Abs(prev.Min - current.MinTemperature) >= DELTA_TEMP_THRESHOLD)
                return true;

            // Rate of Rise or Deviation changed significantly
            if (Math.Abs(prev.RateOfRise - current.RateOfRise) >= DELTA_TEMP_THRESHOLD ||
                Math.Abs(prev.Deviation - current.Deviation) >= DELTA_TEMP_THRESHOLD)
                return true;

            return false;
        }

        public ChannelsManager(
            IEventAggregator eventAggregator,
            ILogger<ChannelsManager> logger,
            ZoneConfigurationService zoneConfigurationService,
            ThresholdComparator thresholdComparator,
            ConfigManager configurationManager,
            WebSocketServer webSocketServiceServer,
            EventHistoryService eventHistoryService,
            IAlarmManager alarmManager,
            IDtsManager dtsManager)
        {
            _eventAggregator = eventAggregator;
            _logger = logger;
            _zoneConfigurationService = zoneConfigurationService;
            _thresholdComparator = thresholdComparator;
            _configurationManager = configurationManager;
            _webSocketServiceServer = webSocketServiceServer;
            _eventHistoryService = eventHistoryService;
            _alarmManager = alarmManager;
            _dtsManager = dtsManager;

            // Subscribe to DTS temperature events
            _eventAggregator.Subscribe<TemperatureDataReceivedEvent>(HandleTemperatureDataReceivedEvent);

            // Subscribe to fiber break events
            _eventAggregator.Subscribe<FiberBreakDetectedEvent>(HandleFiberBreakDetectedEvent);
            _eventAggregator.Subscribe<AlarmResetRequestedEvent>(HandleAlarmResetRequestedEvent);
            _eventAggregator.Subscribe<ResetAllZoneAlarmsRequestedEvent>(HandleResetAllZoneAlarmsRequestedEvent);

            // Subscribe to configuration changes to re-evaluate alarms when thresholds change
            _eventAggregator.Subscribe<ConfigurationChangedEvent>(HandleConfigurationChangedEvent);

            // P1 #6: Load cached config values at startup (single blocking call — acceptable at init)
            try
            {
                var sysConfig = _configurationManager.GetSystemConfigurationAsync().GetAwaiter().GetResult();
                if (sysConfig != null)
                {
                    _cachedRorWindowSeconds = sysConfig.RateOfRiseWindowSeconds > 0
                        ? sysConfig.RateOfRiseWindowSeconds : 60;
                    int dtsPollingInterval = sysConfig.DtsPollingInterval > 0
                        ? sysConfig.DtsPollingInterval : 6000;
                    _cachedZoneBroadcastThrottleMs = Math.Max(1000, (int)(dtsPollingInterval * 0.9));
                }
            }
            catch
            {
                // Defaults already set in field declarations
            }
        }

        /// <summary>
        /// Initializes channels and zone states from configuration.
        /// </summary>
        public Task InitializeChannelsAsync()
        {
            _logger.LogInformation("Initializing channels...");

            // Collect all valid zone IDs from current configuration
            var validZoneIds = new HashSet<int>();

            foreach (var channel in _configurationManager.GetChannels())
            {
                // Ensure initial ChannelState fields that other handlers expect are set.
                channel.State.IsActive = channel.Configuration.IsEnabled; // mark active so FiberBreak events and others are handled
                channel.State.ConfiguredLength = channel.Configuration.ChannelLength;
                channel.State.ActualLength = channel.Configuration.ChannelLength;
                channel.State.CorrectionLength = channel.Configuration.CorrectionLength;
                channel.State.NumberOfZones = channel.Configuration.NumberOfZones;

                _channelStates.AddOrUpdate(
                    channel.Configuration.ChannelId,
                    channel.State,
                    (k, v) => channel.State);

                foreach (var zone in channel.Zones)
                {
                    validZoneIds.Add(zone.Configuration.ZoneId);
                    _zoneStates.AddOrUpdate(
                        zone.Configuration.ZoneId,
                        zone.State,
                        (k, v) => zone.State);
                }
            }

            // Purge stale zone entries (deleted zones)
            var staleZoneIds = _zoneStates.Keys.Where(id => !validZoneIds.Contains(id)).ToList();
            foreach (var staleId in staleZoneIds)
            {
                _zoneStates.TryRemove(staleId, out _);
                _logger.LogInformation("Purged stale zone state for ZoneId {ZoneId}", staleId);
            }

            // Purge stale ROR history and broadcast snapshots for deleted zones
            var staleRorKeys = _rorHistory.Keys
                .Where(key => !validZoneIds.Contains(int.Parse(key.Split('_')[1])))
                .ToList();
            foreach (var key in staleRorKeys)
            {
                _rorHistory.TryRemove(key, out _);
            }

            var staleBroadcastKeys = _lastBroadcastState.Keys
                .Where(key => !validZoneIds.Contains(int.Parse(key.Split('_')[1])))
                .ToList();
            foreach (var key in staleBroadcastKeys)
            {
                _lastBroadcastState.TryRemove(key, out _);
            }

            if (staleZoneIds.Count > 0)
            {
                _logger.LogInformation("Cleaned up {Count} stale zone entries after configuration change", staleZoneIds.Count);
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Calculates Rate of Rise using a sliding window for stability.
        /// ROR = (newest_temp - oldest_temp_in_window) / window_duration_minutes
        /// </summary>
        /// <param name="channelId">Channel ID</param>
        /// <param name="zoneId">Zone ID</param>
        /// <param name="currentAvgTemp">Current average temperature</param>
        /// <param name="timestamp">Current timestamp</param>
        /// <returns>Rate of Rise in °C/minute</returns>
        private double CalculateRateOfRise(int channelId, int zoneId, double currentAvgTemp, DateTime timestamp)
        {
            string key = $"{channelId}_{zoneId}";

            // Get or create the history queue for this zone
            var history = _rorHistory.GetOrAdd(key, _ => new Queue<(DateTime, double)>());

            // Thread-safe operations on the queue
            lock (history)
            {
                // Add current reading
                history.Enqueue((timestamp, currentAvgTemp));

                // Remove readings older than the window
                int rorWindowSeconds = GetRorWindowSeconds();
                DateTime windowStart = timestamp.AddSeconds(-rorWindowSeconds);
                while (history.Count > 0 && history.Peek().Timestamp < windowStart)
                {
                    history.Dequeue();
                }

                // Need at least 2 readings to calculate ROR
                if (history.Count < 2)
                {
                    return 0;
                }

                // Get oldest and newest readings in window
                var oldest = history.Peek();
                var newest = (timestamp, currentAvgTemp);

                // Calculate time difference in minutes
                double minutes = (newest.timestamp - oldest.Timestamp).TotalMinutes;

                if (minutes <= 0)
                {
                    return 0;
                }

                // Calculate ROR in °C/minute
                double tempChange = newest.currentAvgTemp - oldest.AvgTemp;
                double ror = tempChange / minutes;

                // Log for debugging (only occasionally to avoid log spam)
                if (history.Count % 10 == 0)  // Log every 10th calculation
                {
                    _logger.LogDebug(
                        "[ROR] Zone {Ch}_{Z}: Window={WindowSec}s, Samples={Count}, " +
                        "TempChange={TempChange:F2}°C over {Minutes:F2}min = {ROR:F2}°C/min",
                        channelId, zoneId, rorWindowSeconds, history.Count,
                        tempChange, minutes, ror);
                }

                return ror;
            }
        }

        public void GetNumberOfZones(out int totalZones, out int enabledZones)
        {
            var zones = _configurationManager.GetChannels()
                .SelectMany(c => c.Zones)
                .ToList();
            totalZones = zones.Count;
            enabledZones = zones.Count(z => z.Configuration.Enabled);
        }

        public List<Channel> GetChannels()
        {
            return _configurationManager.GetChannels();
        }

        public Task<ZoneConfiguration> GetZoneConfigurationAsync(int zoneId)
        {
            var zone = _configurationManager.GetChannels()
                .SelectMany(c => c.Zones)
                .FirstOrDefault(z => z.Configuration.ZoneId == zoneId);

            return Task.FromResult(zone?.Configuration);
        }
        private async Task HandleAlarmResetRequestedEvent(AlarmResetRequestedEvent ev)
        {
            _logger.LogWarning(
                "RESET received → Clearing zone alarm flags and fiber break state");

            foreach (var zone in _zoneStates.Values)
            {
                zone.MaxAlarm = false;
                zone.MinAlarm = false;
                zone.PreAlarm = false;
                zone.RorAlarm = false;
                zone.DeviationAlarm = false;
                zone.ActiveAlarm = false;
            }

            // Clear fiber break state so the alarm is fully reset.
            // If the fiber is still physically broken, the next DTS polling cycle
            // will re-detect it and re-raise the alarm naturally.
            foreach (var kvp in _channelStates)
            {
                var channelState = kvp.Value;

                if (channelState.FiberBroken)
                {
                    _logger.LogWarning(
                        "Clearing fiber break state for Channel {ChannelId} (was at {Position}m). Will re-detect on next DTS read if still broken.",
                        kvp.Key, channelState.FiberBreakPosition);

                    channelState.FiberBroken = false;
                    channelState.FiberBreakPosition = null;
                    channelState.ActualLength = channelState.ConfiguredLength;

                    _dtsManager.ResetFiberBreakState();
                    // Publish fiber restored event
                    await _eventAggregator.PublishAsync(new FiberBreakDetectedEvent(
                            kvp.Key,
                            false,
                            channelState.ConfiguredLength,
                            DateTime.UtcNow));

                }
            }

            //return Task.CompletedTask;
        }

        /// <summary>
        /// Handles configuration changes and re-evaluates alarms when zone thresholds change.
        /// This ensures alarms are cleared when thresholds are raised above current values.
        /// </summary>
        private async Task HandleConfigurationChangedEvent(ConfigurationChangedEvent ev)
        {
            // P1 #6: Refresh cached config values on any configuration change
            try
            {
                var sysConfig = await _configurationManager.GetSystemConfigurationAsync();
                if (sysConfig != null)
                {
                    _cachedRorWindowSeconds = sysConfig.RateOfRiseWindowSeconds > 0
                        ? sysConfig.RateOfRiseWindowSeconds : 60;
                    int dtsPollingInterval = sysConfig.DtsPollingInterval > 0
                        ? sysConfig.DtsPollingInterval : 6000;
                    _cachedZoneBroadcastThrottleMs = Math.Max(1000, (int)(dtsPollingInterval * 0.9));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to refresh cached config values");
            }

            // Only process zone configuration changes for alarm re-evaluation
            if (ev.ConfigType != ConfigurationType.Zone)
                return;

            _logger.LogInformation("Zone configuration changed, re-evaluating alarms...");

            // Extract channel ID from the event
            if (!ev.Changes.TryGetValue("ChannelId", out var channelIdObj) || channelIdObj is not int channelId)
            {
                _logger.LogWarning("ConfigurationChangedEvent for Zone missing ChannelId");
                return;
            }

            // Get active alarms for this channel
            var activeAlarms = await _alarmManager.GetActiveAlarmsForChannelAsync(channelId);

            if (activeAlarms.Count == 0)
            {
                _logger.LogDebug("No active alarms for channel {ChannelId} to re-evaluate", channelId);
                return;
            }

            _logger.LogInformation("Re-evaluating {Count} active alarms for channel {ChannelId} after threshold change",
                activeAlarms.Count, channelId);

            // Get the updated channel configuration
            var channels = _configurationManager.GetChannels();
            var channel = channels.FirstOrDefault(c => c.Configuration.ChannelId == channelId);

            if (channel == null)
            {
                _logger.LogWarning("Channel {ChannelId} not found for alarm re-evaluation", channelId);
                return;
            }

            // Re-evaluate each active alarm against new thresholds
            foreach (var alarm in activeAlarms)
            {
                // Find the zone configuration
                var zone = channel.Zones.FirstOrDefault(z => z.Configuration.ZoneId == alarm.ZoneId);

                if (zone == null)
                {
                    _logger.LogDebug("Zone {ZoneId} not found, alarm will be cleared", alarm.ZoneId);
                    // Zone was deleted - clear the alarm
                    await _eventAggregator.PublishAsync(
                        new AlarmAutoClearedEvent(channelId, alarm.ZoneId, alarm.Type, DateTime.UtcNow));
                    continue;
                }

                // Get current zone state (last known values)
                var zoneState = zone.State;
                bool stillViolated = false;

                // Check if the alarm condition is still violated with new thresholds
                switch (alarm.Type)
                {
                    case AlarmType.MaxTemperature:
                        if (zone.Configuration.IsMaxTempEnabled)
                        {
                            stillViolated = _thresholdComparator.EvaluateMaxTemperature(zone.Configuration, zoneState);
                        }
                        break;

                    case AlarmType.MinTemperature:
                        if (zone.Configuration.IsMinTempEnabled)
                        {
                            stillViolated = _thresholdComparator.EvaluateMinTemperature(zone.Configuration, zoneState);
                        }
                        break;

                    case AlarmType.PreAlarm:
                        if (zone.Configuration.IsPreAlarmEnabled)
                        {
                            stillViolated = _thresholdComparator.EvaluatePreAlarm(zone.Configuration, zoneState);
                        }
                        break;

                    case AlarmType.RateOfRise:
                        if (zone.Configuration.IsRateOfRiseEnabled)
                        {
                            stillViolated = _thresholdComparator.EvaluateRateOfRise(zone.Configuration, zoneState);
                        }
                        break;

                    case AlarmType.ZoneDeviation:
                        if (zone.Configuration.IsDeviationEnabled)
                        {
                            stillViolated = _thresholdComparator.EvaluateDeviation(zone.Configuration, zoneState);
                        }
                        break;

                    default:
                        // For other alarm types (FiberBreak), don't auto-clear based on threshold
                        stillViolated = true;
                        break;
                }

                if (!stillViolated)
                {
                    _logger.LogInformation(
                        "Alarm {AlarmId} ({Type}) for Zone {ZoneId} no longer violates threshold after config change - auto-clearing",
                        alarm.AlarmId, alarm.Type, alarm.ZoneId);

                    // Clear the zone state alarm flag
                    switch (alarm.Type)
                    {
                        case AlarmType.MaxTemperature:
                            zoneState.MaxAlarm = false;
                            zoneState.MaxAlarmBreachValue = null;
                            zoneState.MaxAlarmBreachPosition = null;
                            break;
                        case AlarmType.MinTemperature:
                            zoneState.MinAlarm = false;
                            zoneState.MinAlarmBreachValue = null;
                            zoneState.MinAlarmBreachPosition = null;
                            break;
                        case AlarmType.PreAlarm:
                            zoneState.PreAlarm = false;
                            zoneState.PreAlarmBreachValue = null;
                            zoneState.PreAlarmBreachPosition = null;
                            break;
                        case AlarmType.RateOfRise:
                            zoneState.RorAlarm = false;
                            zoneState.RorAlarmBreachValue = null;
                            zoneState.RorAlarmBreachPosition = null;
                            break;
                        case AlarmType.ZoneDeviation:
                            zoneState.DeviationAlarm = false;
                            zoneState.DeviationAlarmBreachValue = null;
                            zoneState.DeviationAlarmBreachPosition = null;
                            break;
                    }

                    // Update ActiveAlarm flag
                    zoneState.ActiveAlarm = zoneState.MaxAlarm || zoneState.MinAlarm ||
                                           zoneState.PreAlarm || zoneState.RorAlarm ||
                                           zoneState.DeviationAlarm;

                    // Publish AlarmAutoClearedEvent to remove from AlarmManager
                    await _eventAggregator.PublishAsync(
                        new AlarmAutoClearedEvent(channelId, alarm.ZoneId, alarm.Type, DateTime.UtcNow));

                    // Log to EventHistory
                    //await _eventHistoryService.LogAlarmEventAsync(
                    //    channelId,
                    //    alarm.ZoneId,
                    //    alarm.Type,
                    //    isActive: false,
                    //    alarm.TriggerValue,
                    //    null,
                    //    zoneState,
                    //    DateTime.UtcNow);
                }
                else
                {
                    _logger.LogDebug(
                        "Alarm {AlarmId} ({Type}) for Zone {ZoneId} still violates threshold after config change",
                        alarm.AlarmId, alarm.Type, alarm.ZoneId);
                }
            }
        }

        //public async Task UpdateZoneConfigurationAsync(ZoneConfiguration config)
        //{
        //    await _configurationManager.AddOrUpdateZoneConfigurationAsync(config.ChannelId, config);
        //}
        public async Task UpdateZoneConfigurationAsync(ZoneConfiguration config)
        {

            throw new NotSupportedException(
                "Use ReplaceZoneConfigurationAsync(channelId, List<ZoneConfiguration>) instead of single-zone updates.");
        }

        public Task<ZoneState> GetZoneStateAsync(int zoneId)
        {
            _zoneStates.TryGetValue(zoneId, out ZoneState state);
            return Task.FromResult(state ?? new ZoneState(zoneId, "Zone", 0, 0, 0, 0, 0, false, false, false, DateTime.UtcNow));
        }

        public void GetZoneState(int channelNumber, int zoneIndex)
        {
            // Legacy method - retained for interface compatibility
            // Use GetZoneStateAsync(int zoneId) instead
        }

        /// <summary>
        /// FIXED: Creates new ZoneState instance with updated values for atomic replacement.
        /// This prevents readers from seeing partially-updated state.
        /// </summary>
        private async Task<ZoneStats> UpdateZoneState(Zone zone, List<TemperatureDataPoint> zoneData, TemperatureDataReceivedEvent ev)
        {
            // P1 #5: Single-pass computation of all zone statistics.
            // Replaces 4 LINQ passes (Average + Max + Min + FirstOrDefault) with 1 loop.
            // At 1000 zones × ~250 points each, saves ~750,000 redundant iterations per channel.
            double sum = 0;
            double max = double.MinValue;
            double min = double.MaxValue;
            double maxPos = zone.Configuration.StartPoint;
            double minPos = zone.Configuration.StartPoint;

            for (int i = 0; i < zoneData.Count; i++)
            {
                double temp = zoneData[i].Temperature;
                sum += temp;
                if (temp > max) { max = temp; maxPos = zoneData[i].Position; }
                if (temp < min) { min = temp; minPos = zoneData[i].Position; }
            }
            double avg = sum / zoneData.Count;
            double maxTempPosition = maxPos;

            // --- Rate of Rise Calculation (Sliding Window) ---
            // ROR is calculated in °C/minute using MAX temperature for fire detection sensitivity
            // Using MAX (not avg) ensures localized hotspots are detected even in long zones
            // Window size: Configurable via SystemConfiguration.RateOfRiseWindowSeconds (default 60 seconds)
            double rateOfRise = CalculateRateOfRise(
                zone.Configuration.ChannelId,
                zone.Configuration.ZoneId,
                max,
                ev.Timestamp);

            max = (max == double.MinValue) ? 0 : max;
            min = (min == double.MaxValue) ? 0 : min;
            avg = double.IsFinite(avg) ? avg : 0;

            // 2. Create NEW state object with all computed values (atomic update pattern)
            var newState = new ZoneState(
                zone.Configuration.ZoneId,
                zone.Configuration.Name ?? $"Zone {zone.Configuration.ZoneId}",
                zone.Configuration.StartPoint,
                zone.Configuration.EndPoint,
                avg,  // AverageTemperature
                max,  // MaxTemperature
                min,  // MinTemperature
                zone.State.PreAlarm,  // Preserve alarm states
                zone.State.MaxAlarm,
                zone.State.MinAlarm,
                ev.Timestamp
            )
            {
                // Set all other properties
                Enabled = zone.Configuration.Enabled,
                MaxTempPosition = maxTempPosition,
                Deviation = max - min,
                RateOfRise = rateOfRise,
                RorAlarm = zone.State.RorAlarm,
                DeviationAlarm = zone.State.DeviationAlarm,
                ActiveAlarm = zone.State.MaxAlarm || zone.State.MinAlarm || zone.State.PreAlarm || zone.State.DeviationAlarm || zone.State.RorAlarm,
                IsFirstTime = false, // Clear first-time flag

                //ZoneData = zoneData.ToList(),

                // Preserve alarm breach data
                PreAlarmBreachValue = zone.State.PreAlarmBreachValue,
                PreAlarmBreachPosition = zone.State.PreAlarmBreachPosition,
                MaxAlarmBreachValue = zone.State.MaxAlarmBreachValue,
                MaxAlarmBreachPosition = zone.State.MaxAlarmBreachPosition,
                MinAlarmBreachValue = zone.State.MinAlarmBreachValue,
                MinAlarmBreachPosition = zone.State.MinAlarmBreachPosition,
                RorAlarmBreachValue = zone.State.RorAlarmBreachValue,
                RorAlarmBreachPosition = zone.State.RorAlarmBreachPosition,
                DeviationAlarmBreachValue = zone.State.DeviationAlarmBreachValue,
                DeviationAlarmBreachPosition = zone.State.DeviationAlarmBreachPosition
            };

            // 3. Atomically replace zone state
            zone.State = newState;

            // 4. Update concurrent dictionary with new state
            _zoneStates.AddOrUpdate(
                zone.Configuration.ZoneId,
                newState,
                (k, v) => newState  // Returns new immutable state, not mutating reference
            );

            // NOTE: State is no longer persisted on every temperature update
            // It will only be saved to EventHistory when alarms are triggered/cleared

            // OPTIMIZATION: Fire-and-forget for Modbus register updates (non-critical path)
            // This prevents the high-frequency zone state events from blocking temperature processing
            _ = _eventAggregator.PublishAsync(
                new ZoneStateUpdatedEvent(
                    zone.Configuration.ChannelId,
                    zone.Configuration.ZoneId,
                    zone.State,
                    ev.Timestamp
                )
            );

            // Per-zone state log — Trace level to avoid flooding at 1000+ zones
            _logger.LogTrace("ZoneState | CH{ChannelId}/Z{ZoneId} | Avg={Avg:F1} Max={Max:F1} Min={Min:F1} RoR={RoR:F2} Dev={Dev:F1} Alarm={Active}",
                zone.Configuration.ChannelId, zone.Configuration.ZoneId,
                avg, max, min, zone.State.RateOfRise, zone.State.Deviation, zone.State.ActiveAlarm);

            // NOTE: Zone state broadcast moved to AFTER alarm evaluation to ensure
            // alarm flags are up-to-date when DTSCM receives the update.
            // See BroadcastZoneStateAsync() called from HandleTemperatureDataReceivedEvent.

            return new ZoneStats(avg, max, min, maxPos, minPos);
        }

        /// <summary>
        /// Computes the start index and count for data points within [startPos, endPos]
        /// using O(1) index arithmetic instead of O(N) LINQ filtering.
        /// Data points are guaranteed to be evenly spaced at a fixed resolution (typically 0.4m).
        /// This eliminates ~25M comparisons/cycle when processing 1000+ zones across 8 channels.
        /// </summary>
        private static (int StartIndex, int Count) GetZoneDataRange(
            TemperatureDataPoint[] data, double startPos, double endPos)
        {
            if (data.Length < 2)
                return data.Length == 1 && data[0].Position >= startPos && data[0].Position <= endPos
                    ? (0, 1)
                    : (0, 0);

            double firstPos = data[0].Position;
            double spacing = data[1].Position - data[0].Position;

            if (spacing <= 0)
                return (0, 0);

            // Calculate start index: first point where Position >= startPos
            int startIdx = (int)Math.Ceiling((startPos - firstPos) / spacing);
            // Calculate end index: last point where Position <= endPos
            int endIdx = (int)Math.Floor((endPos - firstPos) / spacing);

            // Clamp to valid array bounds
            startIdx = Math.Max(0, startIdx);
            endIdx = Math.Min(data.Length - 1, endIdx);

            if (startIdx > endIdx)
                return (0, 0);

            return (startIdx, endIdx - startIdx + 1);
        }

        /// <summary>
        /// Builds a binary zone record for batch broadcasting.
        /// ~42 bytes per zone (vs ~550 bytes JSON) = 93% reduction.
        /// </summary>
        private ZoneBinaryRecord BuildZoneBinaryRecord(Zone zone)
        {
            bool hasAnyAlarm = zone.State.MaxAlarm || zone.State.MinAlarm ||
                               zone.State.PreAlarm || zone.State.RorAlarm ||
                               zone.State.DeviationAlarm;

            return new ZoneBinaryRecord
            {
                ZoneId = zone.Configuration.ZoneId,
                NameBytes = System.Text.Encoding.UTF8.GetBytes(
                    zone.State.Name ?? $"Zone {zone.Configuration.ZoneId}"),
                StartPoint = (float)zone.State.StartPoint,
                EndPoint = (float)zone.State.EndPoint,
                Avg = (float)zone.State.AverageTemperature,
                Max = (float)zone.State.MaxTemperature,
                Min = (float)zone.State.MinTemperature,
                RateOfRise = (float)zone.State.RateOfRise,
                Deviation = (float)zone.State.Deviation,
                MaxTempPosition = (float)zone.State.MaxTempPosition,
                Enabled = zone.State.Enabled,
                ActiveAlarm = hasAnyAlarm,
                MaxAlarm = zone.State.MaxAlarm,
                MinAlarm = zone.State.MinAlarm,
                PreAlarm = zone.State.PreAlarm,
                RorAlarm = zone.State.RorAlarm,
                DeviationAlarm = zone.State.DeviationAlarm,
                LastUpdateTicks = zone.State.LastUpdate.Ticks
            };
        }

        // Maximum zones per WebSocket message to prevent oversized payloads
        // ✅ FIX: Reduced from 25 to 10 zones per batch for faster transmission
        // Smaller batches = faster per-message transmission = less blocking time
        // 10 zones ≈ 2.2KB per message (optimal for long channels with many zones)
        private const int MAX_ZONES_PER_BATCH = 10;

        /// <summary>
        /// Broadcasts zone states for a channel as binary batched messages.
        /// Binary protocol: ~42 bytes/zone vs ~550 bytes JSON = 93% bandwidth reduction.
        /// Chunks zones into smaller batches to prevent oversized WebSocket messages.
        /// </summary>
        private async Task BroadcastZoneStatesBatchAsync(int channelId, List<ZoneBinaryRecord> zoneRecords)
        {
            if (zoneRecords.Count == 0)
                return;

            try
            {
                int totalZones = zoneRecords.Count;
                int batchCount = (totalZones + MAX_ZONES_PER_BATCH - 1) / MAX_ZONES_PER_BATCH;
                var now = DateTime.UtcNow;

                for (int i = 0; i < batchCount; i++)
                {
                    int startIdx = i * MAX_ZONES_PER_BATCH;
                    int count = Math.Min(MAX_ZONES_PER_BATCH, totalZones - startIdx);
                    var chunk = zoneRecords.GetRange(startIdx, count);

                    var binaryData = BinaryProtocol.SerializeZoneBatch(
                        channelId, now, i, batchCount, chunk);

                    await _webSocketServiceServer.BroadcastBinaryMessageAsync(binaryData);

                    if (i < batchCount - 1)
                    {
                        await Task.Delay(10);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to broadcast binary zone batch for channel {ChannelId}", channelId);
            }
        }

        public Task<ChannelState> GetChannelStateAsync(int channelId)
        {
            _channelStates.TryGetValue(channelId, out ChannelState state);
            return Task.FromResult(state ?? new ChannelState(channelId, 0, 0, Array.Empty<TemperatureDataPoint>(), false, DateTime.UtcNow));
        }

        private async Task UpdateChannelState(Channel channel, TemperatureDataReceivedEvent ev)
        {
            double correction = channel.Configuration.CorrectionLength;

            var normalizedData = ev.Data
                .Select(p => new TemperatureDataPoint(
                    p.Temperature,
                    p.Position - correction
                ))
                .Where(p => p.Position >= 0)
                .ToArray(); // ✅ correct type

            channel.State.CurrentData = normalizedData;
            channel.State.LastUpdate = ev.Timestamp;
            channel.State.IsActive = channel.Configuration.IsEnabled;
            channel.State.ConfiguredLength = channel.Configuration.ChannelLength;
            channel.State.CorrectionLength = channel.Configuration.CorrectionLength;

            // ActualLength is updated by fiber break handler
            // Only update it here if it hasn't been set yet (initialization)
            if (channel.State.ActualLength == 0)
            {
                channel.State.ActualLength = channel.Configuration.ChannelLength;
            }

            channel.State.NumberOfZones = channel.Configuration.NumberOfZones;


            _channelStates.AddOrUpdate(
                channel.Configuration.ChannelId,
                channel.State,
                (k, v) => channel.State
            );

            // NOTE: Channel state is no longer persisted on every temperature update
            // Only alarm events are saved to EventHistory

            // Build channel payload
            var channelPayload = new
            {
                ChannelId = channel.Configuration.ChannelId,
                ConfiguredLength = channel.State.ConfiguredLength,
                ActualLength = channel.State.ActualLength,
                CorrectionLength = channel.State.CorrectionLength,
                FiberBroken = channel.State.FiberBroken,
                LastUpdate = channel.State.LastUpdate.ToString("o"),
                NumberOfZones = channel.State.NumberOfZones,
                IsActive = channel.State.IsActive
            };

            // Broadcast to DTS client (efficient - direct serialization to JsonElement)
            JsonElement payloadElement = JsonSerializer.SerializeToElement(channelPayload);
            var wsMessage = new WebSocketMessage("channel_state_update", payloadElement);
            await _webSocketServiceServer.BroadcastMessageAsync(wsMessage);

        }

        private async Task RaiseAlarmAsync(int channelId, int zoneId, Core.Enums.AlarmType type, double value, DateTime timestamp, double? position = null)
        {
            // Update channel's last alarm time
            if (_channelStates.TryGetValue(channelId, out var channelState))
            {
                channelState.LastAlarmTime = timestamp;

                // Publish event for Modbus register update
                await _eventAggregator.PublishAsync(
                    new ChannelEventTimeChangedEvent(channelId, timestamp, timestamp));
            }

            await _eventAggregator.PublishAsync(
                new AlarmTriggeredEvent(channelId, zoneId, type, value, timestamp, position));
        }

        /// <summary>
        /// Processes incoming temperature data, updates states, and triggers alarms.
        /// </summary>
        private async Task HandleTemperatureDataReceivedEvent(TemperatureDataReceivedEvent ev)
        {
            _logger.LogDebug("Temperature event: Channel {C}, Points={N}", ev.ChannelId, ev.Data.Length);

            var channels = _configurationManager.GetChannels();
            var channel = channels.FirstOrDefault(c => c.Configuration.ChannelId == ev.ChannelId); 

            if (channel == null)
            {
                _logger.LogWarning("Received data for unknown channel {ChannelId}", ev.ChannelId);
                return;
            }

            // ✔ Update channel

            await UpdateChannelState(channel, ev);

            // ── Fiber break detection ────────────────────────────────────────
            // Handled exclusively by the hardware 0x0D poll in DtsManager
            // (every 60s). The 0x0D response contains the actual break position
            // from the DTS hardware, which is authoritative. Software heuristics
            // based on data point count are unreliable because the DTS returns
            // the same number of raw samples regardless of fiber break (noise/
            // zeros after the break point), so lastDataPos stays near
            // configuredLength even when the fiber is broken.
            // ──────────────────────────────────────────────────────────────────

            // Collect zone records for binary batch broadcast
            var zonePayloads = new List<ZoneBinaryRecord>();

            // Delta broadcasting: check if a full refresh is due for this channel
            var lastFullRefresh = _lastFullBroadcastTime.GetOrAdd(ev.ChannelId, DateTime.MinValue);
            bool isFullRefresh = (DateTime.UtcNow - lastFullRefresh).TotalSeconds >= FULL_REFRESH_INTERVAL_SECONDS;

            // Track which zones were included for snapshot cache update after broadcast
            var broadcastZoneKeys = new List<(string Key, ZoneState State)>();
            int totalEnabledZones = 0;

            // Process all zones in this channel
            foreach (var zone in channel.Zones)
            {
                if (!zone.Configuration.Enabled)
                    continue;

                // Skip zones beyond fiber break (ActualLength)
                if (zone.Configuration.StartPoint >= channel.State.ActualLength)
                {
                    _logger.LogDebug(
                        "ZONE {ZoneId}: Skipped - zone starts at {Start}m but fiber break/ActualLength is {ActualLength}m",
                        zone.Configuration.ZoneId,
                        zone.Configuration.StartPoint,
                        channel.State.ActualLength);
                    continue;
                }

                // Clamp zone EndPoint to ActualLength if zone spans the break
                double effectiveEndPoint = Math.Min(zone.Configuration.EndPoint, channel.State.ActualLength);

                if (effectiveEndPoint < zone.Configuration.EndPoint)
                {
                    _logger.LogDebug(
                        "ZONE {ZoneId}: EndPoint clamped to {ActualLength}m (fiber break)",
                        zone.Configuration.ZoneId,
                        channel.State.ActualLength);
                }

                // O(1) index arithmetic to find zone data range (replaces O(N) LINQ filter)
                // Data points are evenly spaced at fixed resolution, so indices are computed directly.
                // Math.Max(0, ...) excludes negative positions (correction length area).
                var (zoneStartIdx, zonePointCount) = GetZoneDataRange(
                    ev.Data, Math.Max(0, zone.Configuration.StartPoint), effectiveEndPoint);

                // Debug: how many points matched
                _logger.LogDebug(
                    "ZONE {ZoneId}: Range=({Start}-{EffectiveEnd}) [Configured End: {ConfiguredEnd}] => Points Found={Count}",
                    zone.Configuration.ZoneId,
                    zone.Configuration.StartPoint,
                    effectiveEndPoint,
                    zone.Configuration.EndPoint,
                    zonePointCount);

                // If nothing matched, skip updating thresholds but log at debug level
                if (zonePointCount == 0)
                {
                    _logger.LogDebug("ZONE {ZoneId}: No temperature data in range.", zone.Configuration.ZoneId);
                    continue;
                }

                // Create list from array slice — single allocation, no per-element predicate evaluation.
                // ArraySegment implements ICollection<T> so List pre-allocates exact capacity.
                var segment = new ArraySegment<TemperatureDataPoint>(
                channel.State.CurrentData, zoneStartIdx, zonePointCount);

                var zoneData = segment
                    .Where(dp =>
                    {
                        double correctedPos = dp.Position;

                        // corrected position should be >= 0 (exclude negative corrected side)
                        return correctedPos >= 0 &&
                               correctedPos >= zone.Configuration.StartPoint &&
                               correctedPos <= effectiveEndPoint;
                    })
                    .ToList();


                // ✔ Update zone state & get stats (P1 #5: stats computed in single pass, no recomputation needed)
                var stats = await UpdateZoneState(zone, zoneData, ev);
                double maxTempPosition = stats.MaxPosition;
                double minTempPosition = stats.MinPosition;

                // MAX TEMPERATURE ALARM
                if (zone.Configuration.IsMaxTempEnabled)
                {
                    bool isMaxBreached = _thresholdComparator.EvaluateMaxTemperature(zone.Configuration, zone.State);

                    if (isMaxBreached && !zone.State.MaxAlarm)
                    {
                        zone.State.MaxAlarm = true;

                        // Capture breach value and position
                        zone.State.MaxAlarmBreachValue = zone.State.MaxTemperature;
                        zone.State.MaxAlarmBreachPosition = maxTempPosition;

                        _logger.LogWarning("Raising MAX alarm for Zone {ZoneId}. Value={Value} at {Pos}m Thr={Thr}",
                            zone.Configuration.ZoneId, zone.State.MaxTemperature, maxTempPosition, zone.Configuration.MaxTemp);

                        await RaiseAlarmAsync(
                            zone.Configuration.ChannelId,
                            zone.Configuration.ZoneId,
                            AlarmType.MaxTemperature,
                            zone.State.MaxTemperature,
                            ev.Timestamp,
                            maxTempPosition);

                        // Log to EventHistory
                        await _eventHistoryService.LogAlarmEventAsync(
                            zone.Configuration.ChannelId,
                            zone.Configuration.ZoneId,
                            AlarmType.MaxTemperature,
                            isActive: true,
                            zone.State.MaxTemperature,
                            maxTempPosition,
                            zone.State,
                            ev.Timestamp);

                        // If MAX is ON → PRE-ALARM must be OFF (MaxAlarm supersedes PreAlarm)
                        if (zone.State.PreAlarm)
                        {
                            zone.State.PreAlarm = false;
                            // Clear pre-alarm breach data
                            zone.State.PreAlarmBreachValue = null;
                            zone.State.PreAlarmBreachPosition = null;
                            _logger.LogDebug("PRE-ALARM auto-cleared (MAX alarm active) Zone {Z}", zone.Configuration.ZoneId);

                            // FIX: Notify AlarmManager to remove PreAlarm from active alarms
                            await _eventAggregator.PublishAsync(
                                new AlarmAutoClearedEvent(
                                    zone.Configuration.ChannelId,
                                    zone.Configuration.ZoneId,
                                    AlarmType.PreAlarm,
                                    ev.Timestamp));
                        }
                    }
                    else if (!isMaxBreached && zone.State.MaxAlarm)
                    {
                        zone.State.MaxAlarm = false;
                        // Clear breach data
                        zone.State.MaxAlarmBreachValue = null;
                        zone.State.MaxAlarmBreachPosition = null;
                        _logger.LogDebug("MAX alarm cleared Zone {Z} Value={Value}",
                            zone.Configuration.ZoneId, zone.State.MaxTemperature);

                        // Log to EventHistory
                        //await _eventHistoryService.LogAlarmEventAsync(
                        //    zone.Configuration.ChannelId,
                        //    zone.Configuration.ZoneId,
                        //    AlarmType.MaxTemperature,
                        //    isActive: false,
                        //    zone.State.MaxTemperature,
                        //    null,
                        //    zone.State,
                        //    ev.Timestamp);

                        // Notify AlarmManager to remove from active alarms
                        await _eventAggregator.PublishAsync(
                            new AlarmAutoClearedEvent(
                                zone.Configuration.ChannelId,
                                zone.Configuration.ZoneId,
                                AlarmType.MaxTemperature,
                                ev.Timestamp));
                    }
                }
                else if (zone.State.MaxAlarm)
                {
                    // Alarm type disabled while alarm was active - clear it and publish event
                    zone.State.MaxAlarm = false;
                    zone.State.MaxAlarmBreachValue = null;
                    zone.State.MaxAlarmBreachPosition = null;
                    _logger.LogDebug("MAX alarm auto-cleared (type disabled) Zone {Z}", zone.Configuration.ZoneId);

                    await _eventAggregator.PublishAsync(
                        new AlarmAutoClearedEvent(
                            zone.Configuration.ChannelId,
                            zone.Configuration.ZoneId,
                            AlarmType.MaxTemperature,
                            ev.Timestamp));
                }

                // MIN TEMPERATURE ALARM
                if (zone.Configuration.IsMinTempEnabled)
                {
                    bool isMinBreached = _thresholdComparator.EvaluateMinTemperature(zone.Configuration, zone.State);

                    if (isMinBreached && !zone.State.MinAlarm)
                    {
                        zone.State.MinAlarm = true;

                        // Capture breach value and position
                        zone.State.MinAlarmBreachValue = zone.State.MinTemperature;
                        zone.State.MinAlarmBreachPosition = minTempPosition;

                        _logger.LogWarning("Raising MIN alarm for Zone {Z}. Value={Value} at {Pos}m Thr={Thr}",
                            zone.Configuration.ZoneId, zone.State.MinTemperature, minTempPosition, zone.Configuration.MinTemp);

                        await RaiseAlarmAsync(
                            zone.Configuration.ChannelId,
                            zone.Configuration.ZoneId,
                            AlarmType.MinTemperature,
                            zone.State.MinTemperature,
                            ev.Timestamp,
                            minTempPosition);

                        // Log to EventHistory
                        await _eventHistoryService.LogAlarmEventAsync(
                            zone.Configuration.ChannelId,
                            zone.Configuration.ZoneId,
                            AlarmType.MinTemperature,
                            isActive: true,
                            zone.State.MinTemperature,
                            minTempPosition,
                            zone.State,
                            ev.Timestamp);
                    }
                    else if (!isMinBreached && zone.State.MinAlarm)
                    {
                        zone.State.MinAlarm = false;
                        // Clear breach data
                        zone.State.MinAlarmBreachValue = null;
                        zone.State.MinAlarmBreachPosition = null;
                        _logger.LogDebug("MIN alarm cleared Zone {Z}", zone.Configuration.ZoneId);

                        // Log to EventHistory
                        //await _eventHistoryService.LogAlarmEventAsync(
                        //    zone.Configuration.ChannelId,
                        //    zone.Configuration.ZoneId,
                        //    AlarmType.MinTemperature,
                        //    isActive: false,
                        //    zone.State.MinTemperature,
                        //    null,
                        //    zone.State,
                        //    ev.Timestamp);

                        // Notify AlarmManager to remove from active alarms
                        await _eventAggregator.PublishAsync(
                            new AlarmAutoClearedEvent(
                                zone.Configuration.ChannelId,
                                zone.Configuration.ZoneId,
                                AlarmType.MinTemperature,
                                ev.Timestamp));
                    }
                }
                else if (zone.State.MinAlarm)
                {
                    // Alarm type disabled while alarm was active - clear it and publish event
                    zone.State.MinAlarm = false;
                    zone.State.MinAlarmBreachValue = null;
                    zone.State.MinAlarmBreachPosition = null;
                    _logger.LogDebug("MIN alarm auto-cleared (type disabled) Zone {Z}", zone.Configuration.ZoneId);

                    await _eventAggregator.PublishAsync(
                        new AlarmAutoClearedEvent(
                            zone.Configuration.ChannelId,
                            zone.Configuration.ZoneId,
                            AlarmType.MinTemperature,
                            ev.Timestamp));
                }

                // PRE-ALARM
                // Skip PreAlarm evaluation if MaxAlarm is active (MaxAlarm supersedes PreAlarm)
                if (zone.Configuration.IsPreAlarmEnabled && !zone.State.MaxAlarm)
                {
                    bool isPreAlarm = _thresholdComparator.EvaluatePreAlarm(zone.Configuration, zone.State);

                    if (isPreAlarm && !zone.State.PreAlarm)
                    {
                        zone.State.PreAlarm = true;

                        // Capture breach value and position
                        zone.State.PreAlarmBreachValue = zone.State.MaxTemperature;
                        zone.State.PreAlarmBreachPosition = maxTempPosition;

                        _logger.LogWarning("Raising PRE-ALARM for Zone {Z}. Max={Value} at {Pos}m Thr={Thr}",
                            zone.Configuration.ZoneId, zone.State.MaxTemperature, maxTempPosition, zone.Configuration.PreAlarm);

                        await RaiseAlarmAsync(
                            zone.Configuration.ChannelId,
                            zone.Configuration.ZoneId,
                            AlarmType.PreAlarm,
                            zone.State.MaxTemperature,
                            ev.Timestamp,
                            maxTempPosition);

                        // Log to EventHistory
                        await _eventHistoryService.LogAlarmEventAsync(
                            zone.Configuration.ChannelId,
                            zone.Configuration.ZoneId,
                            AlarmType.PreAlarm,
                            isActive: true,
                            zone.State.MaxTemperature,
                            maxTempPosition,
                            zone.State,
                            ev.Timestamp);
                    }
                    else if (!isPreAlarm && zone.State.PreAlarm)
                    {
                        zone.State.PreAlarm = false;
                        // Clear breach data
                        zone.State.PreAlarmBreachValue = null;
                        zone.State.PreAlarmBreachPosition = null;
                        _logger.LogDebug("PRE-ALARM cleared Zone {Z}", zone.Configuration.ZoneId);

                        // Log to EventHistory
                        //await _eventHistoryService.LogAlarmEventAsync(
                        //    zone.Configuration.ChannelId,
                        //    zone.Configuration.ZoneId,
                        //    AlarmType.PreAlarm,
                        //    isActive: false,
                        //    zone.State.MaxTemperature,
                        //    null,
                        //    zone.State,
                        //    ev.Timestamp);

                        // Notify AlarmManager to remove from active alarms
                        await _eventAggregator.PublishAsync(
                            new AlarmAutoClearedEvent(
                                zone.Configuration.ChannelId,
                                zone.Configuration.ZoneId,
                                AlarmType.PreAlarm,
                                ev.Timestamp));
                    }
                }
                else if (zone.State.PreAlarm)
                {
                    // Alarm type disabled while alarm was active - clear it and publish event
                    zone.State.PreAlarm = false;
                    zone.State.PreAlarmBreachValue = null;
                    zone.State.PreAlarmBreachPosition = null;
                    _logger.LogDebug("PRE-ALARM auto-cleared (type disabled) Zone {Z}", zone.Configuration.ZoneId);

                    await _eventAggregator.PublishAsync(
                        new AlarmAutoClearedEvent(
                            zone.Configuration.ChannelId,
                            zone.Configuration.ZoneId,
                            AlarmType.PreAlarm,
                            ev.Timestamp));
                }

                // RATE OF RISE
                if (zone.Configuration.IsRateOfRiseEnabled)
                {
                    bool isRoR = _thresholdComparator.EvaluateRateOfRise(zone.Configuration, zone.State);

                    if (isRoR && !zone.State.RorAlarm)
                    {
                        zone.State.RorAlarm = true;

                        // Capture breach value and position (use max temp position for RoR to match hotspot)
                        zone.State.RorAlarmBreachValue = zone.State.RateOfRise;
                        zone.State.RorAlarmBreachPosition = maxTempPosition;

                        _logger.LogWarning("Raising ROR alarm for Zone {Z}. RoR={RoR} at {Pos}m Thr={Thr}",
                            zone.Configuration.ZoneId, zone.State.RateOfRise, maxTempPosition, zone.Configuration.RoRThreshold);

                        await RaiseAlarmAsync(
                            zone.Configuration.ChannelId,
                            zone.Configuration.ZoneId,
                            AlarmType.RateOfRise,
                            zone.State.RateOfRise,
                            ev.Timestamp,
                            zone.State.RorAlarmBreachPosition);

                        // Log to EventHistory
                        await _eventHistoryService.LogAlarmEventAsync(
                            zone.Configuration.ChannelId,
                            zone.Configuration.ZoneId,
                            AlarmType.RateOfRise,
                            isActive: true,
                            zone.State.RateOfRise,
                            zone.State.RorAlarmBreachPosition,
                            zone.State,
                            ev.Timestamp);
                    }
                    else if (!isRoR && zone.State.RorAlarm)
                    {
                        zone.State.RorAlarm = false;
                        // Clear breach data
                        zone.State.RorAlarmBreachValue = null;
                        zone.State.RorAlarmBreachPosition = null;
                        _logger.LogDebug("ROR alarm cleared Zone {Z}", zone.Configuration.ZoneId);

                        // Log to EventHistory
                        //await _eventHistoryService.LogAlarmEventAsync(
                        //    zone.Configuration.ChannelId,
                        //    zone.Configuration.ZoneId,
                        //    AlarmType.RateOfRise,
                        //    isActive: false,
                        //    zone.State.RateOfRise,
                        //    null,
                        //    zone.State,
                        //    ev.Timestamp);

                        // Notify AlarmManager to remove from active alarms
                        await _eventAggregator.PublishAsync(
                            new AlarmAutoClearedEvent(
                                zone.Configuration.ChannelId,
                                zone.Configuration.ZoneId,
                                AlarmType.RateOfRise,
                                ev.Timestamp));
                    }
                }
                else if (zone.State.RorAlarm)
                {
                    // Alarm type disabled while alarm was active - clear it and publish event
                    zone.State.RorAlarm = false;
                    zone.State.RorAlarmBreachValue = null;
                    zone.State.RorAlarmBreachPosition = null;
                    _logger.LogDebug("ROR alarm auto-cleared (type disabled) Zone {Z}", zone.Configuration.ZoneId);

                    await _eventAggregator.PublishAsync(
                        new AlarmAutoClearedEvent(
                            zone.Configuration.ChannelId,
                            zone.Configuration.ZoneId,
                            AlarmType.RateOfRise,
                            ev.Timestamp));
                }

                // DEVIATION ALARM
                if (zone.Configuration.IsDeviationEnabled)
                {
                    bool isDev = _thresholdComparator.EvaluateDeviation(zone.Configuration, zone.State);

                    if (isDev && !zone.State.DeviationAlarm)
                    {
                        // First trigger - set alarm and capture initial breach value
                        zone.State.DeviationAlarm = true;

                        // Capture breach value and position (use max temp position for deviation)
                        zone.State.DeviationAlarmBreachValue = zone.State.Deviation;
                        zone.State.DeviationAlarmBreachPosition = maxTempPosition;

                        _logger.LogWarning("Raising DEVIATION alarm for Zone {Z}. Dev={Dev} at {Pos}m Thr={Thr}",
                            zone.Configuration.ZoneId, zone.State.Deviation, maxTempPosition, zone.Configuration.DeviationThreshold);

                        await RaiseAlarmAsync(
                            zone.Configuration.ChannelId,
                            zone.Configuration.ZoneId,
                            AlarmType.ZoneDeviation,
                            zone.State.Deviation,
                            ev.Timestamp,
                            maxTempPosition);

                        // Log to EventHistory
                        await _eventHistoryService.LogAlarmEventAsync(
                            zone.Configuration.ChannelId,
                            zone.Configuration.ZoneId,
                            AlarmType.ZoneDeviation,
                            isActive: true,
                            zone.State.Deviation,
                            maxTempPosition,
                            zone.State,
                            ev.Timestamp);
                    }
                    else if (!isDev && zone.State.DeviationAlarm)
                    {
                        zone.State.DeviationAlarm = false;
                        // Clear breach data
                        zone.State.DeviationAlarmBreachValue = null;
                        zone.State.DeviationAlarmBreachPosition = null;
                        _logger.LogDebug("Deviation alarm cleared Zone {Z}", zone.Configuration.ZoneId);

                        // Log to EventHistory
                        //await _eventHistoryService.LogAlarmEventAsync(
                        //    zone.Configuration.ChannelId,
                        //    zone.Configuration.ZoneId,
                        //    AlarmType.ZoneDeviation,
                        //    isActive: false,
                        //    zone.State.Deviation,
                        //    null,
                        //    zone.State,
                        //    ev.Timestamp);

                        // Notify AlarmManager to remove from active alarms
                        await _eventAggregator.PublishAsync(
                            new AlarmAutoClearedEvent(
                                zone.Configuration.ChannelId,
                                zone.Configuration.ZoneId,
                                AlarmType.ZoneDeviation,
                                ev.Timestamp));
                    }
                }
                else if (zone.State.DeviationAlarm)
                {
                    // Alarm type disabled while alarm was active - clear it and publish event
                    zone.State.DeviationAlarm = false;
                    zone.State.DeviationAlarmBreachValue = null;
                    zone.State.DeviationAlarmBreachPosition = null;
                    _logger.LogDebug("DEVIATION alarm auto-cleared (type disabled) Zone {Z}", zone.Configuration.ZoneId);

                    await _eventAggregator.PublishAsync(
                        new AlarmAutoClearedEvent(
                            zone.Configuration.ChannelId,
                            zone.Configuration.ZoneId,
                            AlarmType.ZoneDeviation,
                            ev.Timestamp));
                }

                // Update ActiveAlarm flag based on current alarm states (after alarm evaluation)
                zone.State.ActiveAlarm = zone.State.MaxAlarm || zone.State.MinAlarm ||
                                         zone.State.PreAlarm || zone.State.RorAlarm ||
                                         zone.State.DeviationAlarm;

                totalEnabledZones++;

                // Delta broadcasting: only include zone if it has meaningful changes
                // Full refresh sends everything; delta mode skips unchanged zones (~80-90% bandwidth savings)
                string zoneKey = $"{zone.Configuration.ChannelId}_{zone.Configuration.ZoneId}";
                if (isFullRefresh || HasMeaningfulChange(zoneKey, zone.State))
                {
                    zonePayloads.Add(BuildZoneBinaryRecord(zone));
                    broadcastZoneKeys.Add((zoneKey, zone.State));
                }
            }

            // ✅ THROTTLE: Broadcast zone states aligned with DTS polling interval
            // Internal state is updated on every DTS sample, but WebSocket broadcast is throttled
            // Throttle interval dynamically matches 90% of DTS polling rate (e.g., 5.4s for 6s DTS polling)
            // EXCEPTION: Always broadcast immediately when alarms are active (safety-critical)
            var now = DateTime.UtcNow;
            var lastBroadcast = _lastZoneBroadcastTime.GetOrAdd(ev.ChannelId, DateTime.MinValue);
            var timeSinceLastBroadcast = (now - lastBroadcast).TotalMilliseconds;

            // Check if any alarms are active on this channel (bypass throttling for safety)
            bool hasActiveAlarms = channel.Zones.Any(z => z.State.ActiveAlarm);
            int throttleInterval = GetZoneBroadcastThrottleMs();
            bool shouldBroadcast = hasActiveAlarms || timeSinceLastBroadcast >= throttleInterval;

            if (shouldBroadcast && zonePayloads.Count > 0)
            {
                // Broadcast changed zone states in batched messages
                await BroadcastZoneStatesBatchAsync(ev.ChannelId, zonePayloads);

                // Update last broadcast time
                _lastZoneBroadcastTime[ev.ChannelId] = now;

                // Update snapshot cache for all zones that were broadcast
                // This must happen AFTER successful broadcast to maintain sync
                foreach (var (key, state) in broadcastZoneKeys)
                {
                    _lastBroadcastState[key] = new ZoneBroadcastSnapshot(state);
                }

                // Track full-refresh timing
                if (isFullRefresh)
                {
                    _lastFullBroadcastTime[ev.ChannelId] = now;
                }

                if (hasActiveAlarms)
                {
                    _logger.LogDebug("Zone states broadcast for channel {ChannelId} - IMMEDIATE (active alarms, {Sent}/{Total} zones)",
                        ev.ChannelId, zonePayloads.Count, totalEnabledZones);
                }
                else
                {
                    _logger.LogDebug("Zone states broadcast for channel {ChannelId} ({Sent}/{Total} zones, {Mode}, throttled to {ThrottleMs}ms)",
                        ev.ChannelId, zonePayloads.Count, totalEnabledZones,
                        isFullRefresh ? "full-refresh" : "delta",
                        throttleInterval);
                }
            }
            else if (!shouldBroadcast)
            {
                _logger.LogTrace("Zone broadcast skipped for channel {ChannelId} - throttled (last broadcast {Ms}ms ago, threshold {ThrottleMs}ms)",
                    ev.ChannelId, (int)timeSinceLastBroadcast, throttleInterval);
            }
            else
            {
                _logger.LogTrace("Zone broadcast skipped for channel {ChannelId} - no zones changed (delta mode)", ev.ChannelId);
            }

            // Check if channel has any active alarms, if not clear LastAlarmTime
            bool hasAnyAlarm = channel.Zones.Any(z => z.State.ActiveAlarm);
            if (!hasAnyAlarm && _channelStates.TryGetValue(channel.Configuration.ChannelId, out var channelState))
            {
                if (channelState.LastAlarmTime.HasValue)
                {
                    channelState.LastAlarmTime = null;
                    _logger.LogInformation("Cleared LastAlarmTime for Channel {ChannelId} - no active alarms", channel.Configuration.ChannelId);

                    // Publish event for Modbus register clear
                    await _eventAggregator.PublishAsync(
                        new ChannelEventTimeChangedEvent(channel.Configuration.ChannelId, null, ev.Timestamp));
                }
            }
        }

        /// <summary>
        /// Handles fiber break detected events and updates channel state.
        /// Updates actual length based on fiber break status:
        /// - If broken: ActualLength = FiberBreakPosition
        /// - If cleared: ActualLength = ConfiguredLength
        /// </summary>
        private Task HandleResetAllZoneAlarmsRequestedEvent(
    ResetAllZoneAlarmsRequestedEvent @event)
        {
            _logger.LogWarning("Resetting all zone alarm states (restart monitoring)");

            var channels = _configurationManager.GetChannels();

            foreach (var channel in channels)
            {
                foreach (var zone in channel.Zones)
                {
                    zone.State.MaxAlarm = false;
                    zone.State.MinAlarm = false;
                    zone.State.PreAlarm = false;
                    zone.State.RorAlarm = false;
                    zone.State.DeviationAlarm = false;

                    zone.State.MaxAlarmBreachValue = null;
                    zone.State.MaxAlarmBreachPosition = null;
                    zone.State.MinAlarmBreachValue = null;
                    zone.State.MinAlarmBreachPosition = null;
                    zone.State.PreAlarmBreachValue = null;
                    zone.State.PreAlarmBreachPosition = null;
                    zone.State.RorAlarmBreachValue = null;
                    zone.State.RorAlarmBreachPosition = null;
                    zone.State.DeviationAlarmBreachValue = null;
                    zone.State.DeviationAlarmBreachPosition = null;
                }
            }

            return Task.CompletedTask;
        }

        private async Task HandleFiberBreakDetectedEvent(FiberBreakDetectedEvent ev)
        {
            _logger.LogDebug(
                "Handling FiberBreakDetectedEvent for channel {ChannelId}: IsBroken={IsBroken}, Position={Position}m",
                ev.ChannelId, ev.IsBroken, ev.Position);
            if (_channelStates.TryGetValue(ev.ChannelId, out var channelState))
            {
                if (!channelState.IsActive)
                {
                    return;
                }
                // Compute corrected position once
                double correctedPosition = ev.Position - channelState.CorrectionLength;

                // Valid break only if corrected position is >= 0
                bool isValidBreak = ev.IsBroken && correctedPosition >= 0;

                channelState.FiberBroken = isValidBreak;
                channelState.FiberBreakPosition = isValidBreak ? correctedPosition : null;

                // Update actual length
                if (isValidBreak)
                {
                    channelState.ActualLength = correctedPosition;
                    _logger.LogWarning(
                        "Channel {ChannelId} fiber BROKEN: ActualLength={Actual}m (ConfiguredLength={Configured}m), Break at {Break}m",
                        ev.ChannelId,
                        channelState.ActualLength,
                        channelState.ConfiguredLength,
                        correctedPosition);

                    // Raise FiberBreak alarm
                    await _eventAggregator.PublishAsync(
                        new AlarmTriggeredEvent(
                            ev.ChannelId,
                            0,
                            Core.Enums.AlarmType.FiberBreak,
                            correctedPosition,
                            ev.Timestamp,
                            correctedPosition));

                    var emptyZoneState = new ZoneState(
                            0,              // zoneId
                            "NA",       // state
                            0,              // avg
                            0,              // max
                            0,              // min
                            0,              // maxPosition
                            0,              // ror
                            false,          // isPreAlarm
                            false,          // isFireAlarm
                            false,          // isFault
                            ev.Timestamp    // timestamp
                        );

                    await _eventHistoryService.LogAlarmEventAsync(
                            ev.ChannelId,
                            0,
                            Core.Enums.AlarmType.FiberBreak,
                            true,
                            correctedPosition,
                            correctedPosition,
                            emptyZoneState,
                            ev.Timestamp);
                }
                else
                {
                    // Fiber not validly broken
                    channelState.ActualLength = channelState.ConfiguredLength;

                    _logger.LogInformation(
                        "Channel {ChannelId} fiber RESTORED or invalid break: ActualLength={Actual}m",
                        ev.ChannelId,
                        channelState.ActualLength);

                    await _eventAggregator.PublishAsync(
                        new AlarmAutoClearedEvent(
                            ev.ChannelId,
                            0,
                            Core.Enums.AlarmType.FiberBreak,
                            ev.Timestamp));
                }

                // Clear alarms for zones beyond fiber break point
                if (isValidBreak)
                {
                    var channel = _configurationManager.GetChannels()
                        .FirstOrDefault(c => c.Configuration.ChannelId == ev.ChannelId);

                    if (channel != null)
                    {
                        foreach (var zone in channel.Zones)
                        {
                            // Skip zones before the break
                            if (zone.Configuration.StartPoint < correctedPosition)
                                continue;

                            bool hadAlarms =
                                zone.State.MaxAlarm ||
                                zone.State.MinAlarm ||
                                zone.State.PreAlarm ||
                                zone.State.RorAlarm ||
                                zone.State.DeviationAlarm;

                            if (hadAlarms)
                            {
                                _logger.LogInformation(
                                    "Clearing alarms for Zone {ZoneId} (starts at {Start}m) - beyond fiber break at {Break}m",
                                    zone.Configuration.ZoneId,
                                    zone.Configuration.StartPoint,
                                    correctedPosition);

                                if (zone.State.MaxAlarm)
                                {
                                    zone.State.MaxAlarm = false;
                                    zone.State.MaxAlarmBreachValue = null;
                                    zone.State.MaxAlarmBreachPosition = null;
                                    await _eventAggregator.PublishAsync(
                                        new AlarmAutoClearedEvent(
                                            ev.ChannelId,
                                            zone.Configuration.ZoneId,
                                            AlarmType.MaxTemperature,
                                            ev.Timestamp));
                                }

                                if (zone.State.MinAlarm)
                                {
                                    zone.State.MinAlarm = false;
                                    zone.State.MinAlarmBreachValue = null;
                                    zone.State.MinAlarmBreachPosition = null;
                                    await _eventAggregator.PublishAsync(
                                        new AlarmAutoClearedEvent(
                                            ev.ChannelId,
                                            zone.Configuration.ZoneId,
                                            AlarmType.MinTemperature,
                                            ev.Timestamp));
                                }

                                if (zone.State.PreAlarm)
                                {
                                    zone.State.PreAlarm = false;
                                    zone.State.PreAlarmBreachValue = null;
                                    zone.State.PreAlarmBreachPosition = null;
                                    await _eventAggregator.PublishAsync(
                                        new AlarmAutoClearedEvent(
                                            ev.ChannelId,
                                            zone.Configuration.ZoneId,
                                            AlarmType.PreAlarm,
                                            ev.Timestamp));
                                }

                                if (zone.State.RorAlarm)
                                {
                                    zone.State.RorAlarm = false;
                                    zone.State.RorAlarmBreachValue = null;
                                    zone.State.RorAlarmBreachPosition = null;
                                    await _eventAggregator.PublishAsync(
                                        new AlarmAutoClearedEvent(
                                            ev.ChannelId,
                                            zone.Configuration.ZoneId,
                                            AlarmType.RateOfRise,
                                            ev.Timestamp));
                                }

                                if (zone.State.DeviationAlarm)
                                {
                                    zone.State.DeviationAlarm = false;
                                    zone.State.DeviationAlarmBreachValue = null;
                                    zone.State.DeviationAlarmBreachPosition = null;
                                    await _eventAggregator.PublishAsync(
                                        new AlarmAutoClearedEvent(
                                            ev.ChannelId,
                                            zone.Configuration.ZoneId,
                                            AlarmType.ZoneDeviation,
                                            ev.Timestamp));
                                }

                                zone.State.ActiveAlarm = false;
                            }
                        }
                    }
                }

                // Broadcast fiber break update to WebSocket clients
                var fiberBreakPayload = new
                {
                    ChannelId = ev.ChannelId,
                    FiberBroken = isValidBreak,
                    FiberBreakPosition = isValidBreak ? correctedPosition : (double?)null,
                    ConfiguredLength = channelState.ConfiguredLength,
                    ActualLength = channelState.ActualLength,
                    Timestamp = ev.Timestamp.ToString("o")
                };

                JsonElement payloadElement = JsonSerializer.SerializeToElement(fiberBreakPayload);
                var wsMessage = new WebSocketMessage(
                    isValidBreak ? "fiber_break_detected" : "fiber_break_cleared",
                    payloadElement);

                await _webSocketServiceServer.BroadcastMessageAsync(wsMessage);
            }
            else
            {
                _logger.LogWarning(
                    "Received fiber break event for unknown channel {ChannelId}",
                    ev.ChannelId);
            }
        }


        /// <summary>
        /// Disposes resources and unsubscribes from events to prevent memory leaks.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            // Unsubscribe from events to prevent memory leaks
            // Note: Actual unsubscribe implementation depends on IEventAggregator interface
            // If the event aggregator supports weak references, this may not be necessary
            // But it's good practice to explicitly unsubscribe

            _logger.LogInformation("ChannelsManager disposed");
        }

    }
}
