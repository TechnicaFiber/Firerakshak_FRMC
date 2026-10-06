using FireRakshak.FRMC.Core.Enums;
using FireRakshak.FRMC.Core.EventAggregator;
using FireRakshak.FRMC.Core.Events;
using FireRakshak.FRMC.Core.Models;
using FireRakshak.FRMC.Managers.AlarmManager;
using FireRakshak.FRMC.Managers.RS232Manager;
using FireRakshak.FRMC.Managers.SystemConfigurationManager;
using FireRakshak.FRMC.Managers.SystemHealthManager;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace FireRakshak.FRMC.Managers.RelayManager
{
    /// <summary>
    /// Manages relay control operations, including automatic relay activation on alarms
    /// and manual relay control. Communicates with relay hardware via RS232 commands.
    /// </summary>
    public class RelayControlManager : IRelayControlManager
    {
        private readonly IEventAggregator _eventAggregator;
        private readonly ILogger<RelayControlManager> _logger;
        private readonly IConfigurationManager _configManager;
        private readonly IAlarmManager _alarmManager;
        private readonly IRS232Manager _rs232Manager;
        private readonly ISystemHealthManager _systemHealthManager;


        // Thread-safe relay state storage
        private readonly ConcurrentDictionary<int, RelayState> _relayStates = new();

        // Persistence file path
        private const string RelayStatesFilePath = "Config/relay_states.json";

        // Number of fixed/reserved relays (1-11) not available for zone assignment
        // Relay 1: System Running indicator (always ON when system is active)
        // Relay 2: System Health relay (ON when system faults/health issues)
        // Relay 3: Common Alarm relay (ON when ANY alarm is active in the system)
        // Relays 4-11: Channel FiberBreak relays (Relay 4 = CH1, Relay 5 = CH2, ..., Relay 11 = CH8)
        private const int FIXED_RELAYS_COUNT = 11;

        // Reserved relay assignments
        private const int RELAY_SYSTEM_RUNNING = 1;
        private const int RELAY_SYSTEM_HEALTH = 2;
        private const int RELAY_COMMON_ALARM = 3;
        private const int RELAY_FIBER_BREAK_CH1 = 4;  // CH1 FiberBreak = Relay 4, CH2 = 5, etc.



        /// <summary>
        /// Initializes a new instance of the RelayControlManager class.
        /// </summary>
        public RelayControlManager(
            IEventAggregator eventAggregator,
            ILogger<RelayControlManager> logger,
            IConfigurationManager configManager,
            IAlarmManager alarmManager,
            IRS232Manager rs232Manager,
            ISystemHealthManager systemHealthManager)
        {
            _eventAggregator = eventAggregator;
            _logger = logger;
            _configManager = configManager;
            _alarmManager = alarmManager;
            _rs232Manager = rs232Manager;
            _systemHealthManager = systemHealthManager;

            // Subscribe to configuration changes
            _eventAggregator.Subscribe<ConfigurationChangedEvent>(HandleConfigurationChanged);

            // Subscribe to alarm events
            _eventAggregator.Subscribe<AlarmTriggeredEvent>(HandleAlarmTriggeredEvent);

            _eventAggregator.Subscribe<AlarmAutoClearedEvent>(HandleAlarmAutoClearedEvent);
            _eventAggregator.Subscribe<AlarmClearedEvent>(HandleAlarmClearedEvent);
            _eventAggregator.Subscribe<AlarmResetRequestedEvent>(HandleAlarmResetRequestedEvent);

            // Subscribe to system health fault events (for Relay 2 - System Health)
            _eventAggregator.Subscribe<SystemHealthFaultEvent>(HandleSystemHealthFaultEvent);




            _logger.LogInformation("RelayControlManager initialized. Call InitializeRelays() after configuration loads.");
        }

        /// <summary>
        /// Initializes or re-initializes the relay pool with the specified number of relays.
        /// Uses 1-based relay IDs (relay 1 to relayCount).
        /// </summary>
        public void InitializeRelays(int relayCount)
        {
            _logger.LogInformation(
                "[RELAY] InitializeRelays called with count={Count}",
                relayCount);

            _relayStates.Clear();

            // FIX: Use 1-based relay IDs to match UI and GetAvailableRelays()
            // Relays are numbered 1 to relayCount (not 0 to relayCount-1)
            for (int i = 1; i <= relayCount; i++)
            {
                _relayStates[i] = new RelayState
                {
                    RelayId = i,
                    IsOn = false,
                    ControlSource = RelayControlSource.Auto,
                    LastStateChange = DateTime.UtcNow,
                    LastChangedBy = "System"
                };
            }

            _logger.LogInformation(
                "[RELAY] Pool initialized: IDs 1-{Max} (1-based)",
                relayCount);
        }

        /// <summary>
        /// Handles system configuration changes to reinitialize relay pool if needed.
        /// </summary>
        private async Task HandleConfigurationChanged(ConfigurationChangedEvent @event)
        {
            if (@event.ConfigType == ConfigurationType.System)
            {
                var systemConfig = await _configManager.GetSystemConfigurationAsync();
                if (systemConfig != null)
                {
                    _logger.LogInformation(
                        "[RELAY] Config changed, reinitializing {Count} relays",
                        systemConfig.NumberOfRelays);

                    InitializeRelays(systemConfig.NumberOfRelays);

                    // FIX: Restore saved states after reinitialization
                    await LoadRelayStatesAsync();

                    var availableRelays = GetAvailableRelays();

                    _logger.LogInformation(
                        "[RELAY] Available for assignment: [{Relays}]",
                        string.Join(",", availableRelays));
                }
            }
        }

        /// <summary>
        /// Gets the list of relay IDs available for zone assignment.
        /// Excludes fixed relays (1-11) and already assigned relays.
        /// Optionally excludes a specific zone from the "already assigned" check to allow reassignment.
        /// </summary>
        /// <param name="excludeChannelId">Optional: Channel ID of zone being edited (for reassignment)</param>
        /// <param name="excludeZoneId">Optional: Zone ID of zone being edited (for reassignment)</param>
        public List<int> GetAvailableRelays(int? excludeChannelId = null, int? excludeZoneId = null)
        {
            // FIX #5: Check if relays are initialized
            if (_relayStates.IsEmpty)
            {
                _logger.LogWarning("[RELAY] GetAvailableRelays called but no relays initialized yet");
                return new List<int>();
            }

            // FIX #6: Use ConfigureAwait(false) + GetAwaiter().GetResult() to avoid deadlock
            // This pattern is safe because:
            // 1. ConfigureAwait(false) prevents synchronization context capture
            // 2. GetAwaiter().GetResult() unwraps exceptions properly (unlike .Result)
            SystemConfiguration? systemConfig = null;
            try
            {
                systemConfig = _configManager.GetSystemConfigurationAsync()
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RELAY] Error getting system configuration in GetAvailableRelays");
                return new List<int>();
            }

            if (systemConfig == null)
            {
                _logger.LogWarning("[RELAY] System configuration is null in GetAvailableRelays");
                return new List<int>();
            }

            int totalRelays = systemConfig.NumberOfRelays; // e.g. 48
            int fixedRelays = FIXED_RELAYS_COUNT;          // 11

            if (totalRelays <= fixedRelays)
                return new List<int>();

            // STEP 1: base pool → relays 12 to totalRelays (relays 1-11 are reserved)
            var baseRelays = Enumerable
                .Range(fixedRelays + 1, totalRelays - fixedRelays)
                .ToList(); // UI is 1-based

            // FIX #5: Only include relays that are actually initialized
            var initializedRelays = baseRelays
                .Where(r => _relayStates.ContainsKey(r))
                .ToList();

            if (initializedRelays.Count < baseRelays.Count)
            {
                _logger.LogWarning(
                    "[RELAY] Some relays not initialized. Expected {Expected}, found {Found}",
                    baseRelays.Count, initializedRelays.Count);
            }

            // STEP 2: All non-reserved relays are available
            // Multiple zones can share the same relay - when any zone triggers, the relay activates
            var availableRelays = initializedRelays;

            _logger.LogDebug(
                "[RELAY] Available for zone assignment: [{Relays}]",
                string.Join(",", availableRelays));

            return availableRelays;
        }

        /// <inheritdoc/>
        public async Task CleanupUnassignedRelaysAsync()
        {
            try
            {
                var allZones = _configManager.GetZoneConfigurations();
                var assignedRelayIds = new HashSet<int>(
                    allZones
                        .Where(z => z.AssignedRelayId.HasValue)
                        .Select(z => z.AssignedRelayId!.Value));

                foreach (var relayState in _relayStates.Values
                    .Where(r => r.IsOn && r.RelayId > FIXED_RELAYS_COUNT))
                {
                    if (!assignedRelayIds.Contains(relayState.RelayId))
                    {
                        _logger.LogWarning(
                            "[RELAY] Config cleanup → OFF relay {RelayId} (no longer assigned to any zone)",
                            relayState.RelayId);

                        await TurnRelayOffAsync(
                            relayState.RelayId,
                            RelayControlSource.Auto,
                            "System-ConfigCleanup");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RELAY] Error during unassigned relay cleanup");
            }
        }

        /// <summary>
        /// Clears all relays on startup by sending RS232 OFF commands to hardware.
        /// Ensures hardware matches clean in-memory state after restart.
        /// Skips Relay 1 (System Running) which is set separately.
        /// </summary>
        public async Task ClearAllRelaysOnStartupAsync()
        {
            _logger.LogWarning("[RELAY] Startup: Clearing all relays via RS232...");

            int clearedCount = 0;
            foreach (var relayId in _relayStates.Keys.OrderBy(k => k))
            {
                // Skip System Running relay — handled separately in Program.cs
                if (relayId == RELAY_SYSTEM_RUNNING)
                    continue;

                try
                {
                    await SendRelayCommandAsync(relayId, false);

                    if (_relayStates.TryGetValue(relayId, out var state))
                    {
                        state.IsOn = false;
                        state.ControlSource = RelayControlSource.Auto;
                        state.LastStateChange = DateTime.UtcNow;
                        state.LastChangedBy = "System-Startup-Clear";
                    }

                    clearedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[RELAY] Failed to clear relay {RelayId} on startup", relayId);
                }
            }

            // Also clear heartbeat alarm
            try
            {
                await _rs232Manager.SetHeartbeatAlarmAsync(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RELAY] Failed to clear heartbeat alarm on startup");
            }

            await SaveRelayStatesAsync();
            _logger.LogWarning("[RELAY] Startup: Cleared {Count} relays (all except Relay 1)", clearedCount);
        }

        private async Task HandleAlarmAutoClearedEvent(AlarmAutoClearedEvent @event)
        {
            _logger.LogInformation(
                "[RELAY] Auto-clear check → CH:{ChannelId}, Z:{ZoneId}, Type:{Type}",
                @event.ChannelId, @event.ZoneId, @event.Type);

            // Handle channel-level alarm (FiberBreak) clear using reserved relays
            if (@event.ZoneId == 0)
            {
                await HandleChannelAlarmClear(@event.ChannelId, @event.Type);
            }

            // Exclude the alarm that triggered this event (it may not be removed from AlarmManager yet)
            var activeAlarms = (await _alarmManager.GetActiveAlarmsAsync())
                .Where(a =>
                    !(a.ChannelId == @event.ChannelId &&
                      a.ZoneId == @event.ZoneId &&
                      a.Type == @event.Type))
                .ToList();

            _logger.LogDebug(
                "[RELAY] Active alarms after filtering: {Count} ({Alarms})",
                activeAlarms.Count,
                string.Join(", ", activeAlarms.Select(a => $"CH{a.ChannelId}/Z{a.ZoneId}/{a.Type}")));

            var allZones = _configManager.GetZoneConfigurations();

            var relayToZones = allZones
                .Where(z => z.AssignedRelayId.HasValue)
                .GroupBy(z => z.AssignedRelayId!.Value)
                .ToDictionary(g => g.Key, g => g.ToList());

            _logger.LogDebug(
                "[RELAY] Relay-to-zones mapping: {Count} relays have zones assigned",
                relayToZones.Count);

            // ---- RELAY HANDLING (per relay) ----
            // Only check zone-assigned relays (12+), NOT reserved relays (1-11)
            foreach (var relayState in _relayStates.Values
                .Where(r => r.IsOn && r.ControlSource == RelayControlSource.Auto && r.RelayId > FIXED_RELAYS_COUNT))
            {
                _logger.LogDebug(
                    "[RELAY] Checking relay {RelayId} (ON, Auto)",
                    relayState.RelayId);

                if (!relayToZones.TryGetValue(relayState.RelayId, out var zones))
                {
                    // Relay is ON but no zones are assigned to it
                    // This can happen if zone config changed after alarm triggered
                    // Turn off the relay since there's no reason for it to be ON
                    _logger.LogWarning(
                        "[RELAY] Auto-clear → OFF relay {RelayId} (no zones currently assigned)",
                        relayState.RelayId);

                    await TurnRelayOffAsync(
                        relayState.RelayId,
                        RelayControlSource.Auto,
                        "System-NoZones");
                    continue;
                }

                _logger.LogDebug(
                    "[RELAY] Relay {RelayId} has {ZoneCount} zones: {Zones}",
                    relayState.RelayId,
                    zones.Count,
                    string.Join(", ", zones.Select(z => $"CH{z.ChannelId}/Z{z.ZoneId}")));

                bool anyZoneHasAlarm = zones.Any(z =>
                    activeAlarms.Any(a =>
                        a.ChannelId == z.ChannelId &&
                        a.ZoneId == z.ZoneId));

                _logger.LogDebug(
                    "[RELAY] Relay {RelayId}: anyZoneHasAlarm={HasAlarm}",
                    relayState.RelayId, anyZoneHasAlarm);

                if (!anyZoneHasAlarm)
                {
                    _logger.LogWarning(
                        "[RELAY] Auto-clear → OFF relay {RelayId} (no active alarms for assigned zones)",
                        relayState.RelayId);

                    await TurnRelayOffAsync(
                        relayState.RelayId,
                        RelayControlSource.Auto,
                        "System");
                }
            }

            // ---- STM32 ALARM CLEAR (GLOBAL) ----
            // FIX: Add delay and recheck to handle race condition when multiple alarms clear simultaneously
            // Each AlarmAutoClearedEvent only filters itself, so concurrent clears may see each other as "still active"
            if (!activeAlarms.Any())
            {
                // Small delay to let AlarmManager process other concurrent clear events
                await Task.Delay(100);

                // Recheck actual alarm count from AlarmManager (no filtering needed now)
                var remainingAlarms = await _alarmManager.GetActiveAlarmsAsync();

                if (!remainingAlarms.Any())
                {
                    _logger.LogWarning(
                        "[RELAY] All alarms auto-cleared → STM32 ALARM OFF, Common Alarm OFF");

                    await _rs232Manager.SetHeartbeatAlarmAsync(false);
                    await SetCommonAlarmRelayAsync(false);
                }
                else
                {
                    _logger.LogDebug(
                        "[RELAY] After recheck, {Count} alarms still active - keeping Common Alarm ON",
                        remainingAlarms.Count);
                }
            }
        }

        /// <summary>
        /// Handles manual alarm clear events. Checks if associated relay should be turned off.
        /// </summary>
        private async Task HandleAlarmClearedEvent(AlarmClearedEvent @event)
        {
            _logger.LogInformation(
                "[RELAY] Manual alarm clear → AlarmId:{AlarmId}",
                @event.AlarmId);

            // Get remaining active alarms (the cleared alarm should already be removed)
            var activeAlarms = (await _alarmManager.GetActiveAlarmsAsync()).ToList();

            _logger.LogDebug(
                "[RELAY] Remaining active alarms: {Count} ({Alarms})",
                activeAlarms.Count,
                string.Join(", ", activeAlarms.Select(a => $"CH{a.ChannelId}/Z{a.ZoneId}/{a.Type}")));

            var allZones = _configManager.GetZoneConfigurations();

            var relayToZones = allZones
                .Where(z => z.AssignedRelayId.HasValue)
                .GroupBy(z => z.AssignedRelayId!.Value)
                .ToDictionary(g => g.Key, g => g.ToList());

            // ---- RELAY HANDLING (per relay) ----
            // Only check zone-assigned relays (12+), NOT reserved relays (1-11)
            foreach (var relayState in _relayStates.Values
                .Where(r => r.IsOn && r.ControlSource == RelayControlSource.Auto && r.RelayId > FIXED_RELAYS_COUNT))
            {
                if (!relayToZones.TryGetValue(relayState.RelayId, out var zones))
                {
                    // Relay is ON but no zones are assigned - turn it off
                    _logger.LogWarning(
                        "[RELAY] Manual clear → OFF relay {RelayId} (no zones currently assigned)",
                        relayState.RelayId);

                    await TurnRelayOffAsync(
                        relayState.RelayId,
                        RelayControlSource.Auto,
                        "System-ManualClear-NoZones");
                    continue;
                }

                bool anyZoneHasAlarm = zones.Any(z =>
                    activeAlarms.Any(a =>
                        a.ChannelId == z.ChannelId &&
                        a.ZoneId == z.ZoneId));

                if (!anyZoneHasAlarm)
                {
                    _logger.LogWarning(
                        "[RELAY] Manual clear → OFF relay {RelayId} (no active alarms for assigned zones)",
                        relayState.RelayId);

                    await TurnRelayOffAsync(
                        relayState.RelayId,
                        RelayControlSource.Auto,
                        "System-ManualClear");
                }
            }

            // ---- STM32 ALARM CLEAR (GLOBAL) ----
            // FIX: Add delay and recheck to handle race condition when multiple alarms clear simultaneously
            if (!activeAlarms.Any())
            {
                // Small delay to let AlarmManager finish processing
                await Task.Delay(100);

                // Recheck actual alarm count from AlarmManager
                var remainingAlarms = await _alarmManager.GetActiveAlarmsAsync();

                if (!remainingAlarms.Any())
                {
                    _logger.LogWarning(
                        "[RELAY] All alarms manually cleared → STM32 ALARM OFF, Common Alarm OFF");

                    await _rs232Manager.SetHeartbeatAlarmAsync(false);
                    await SetCommonAlarmRelayAsync(false);
                }
                else
                {
                    _logger.LogDebug(
                        "[RELAY] After recheck, {Count} alarms still active - keeping Common Alarm ON",
                        remainingAlarms.Count);
                }
            }
        }

        private async Task HandleAlarmResetRequestedEvent(AlarmResetRequestedEvent @event)
        {
            _logger.LogWarning(
                "[RELAY] RESET received → turning OFF alarm relays (keeping System Running relay ON)");

            // Turn off all alarm-related relays, but keep Relay 1 (System Running) ON
            foreach (var relay in _relayStates.Values)
            {
                if (relay.IsOn && relay.RelayId != RELAY_SYSTEM_RUNNING)
                {
                    _logger.LogWarning(
                        "[RELAY] RESET → OFF relay {RelayId}",
                        relay.RelayId);

                    await TurnRelayOffAsync(
                        relay.RelayId,
                        RelayControlSource.Auto,
                        "System-Reset");
                }
            }

            // Safety: heartbeat alarm OFF, Common Alarm OFF
            await _rs232Manager.SetHeartbeatAlarmAsync(false);
            await SetCommonAlarmRelayAsync(false);
        }

        /// <summary>
        /// Handles system health fault events to control Relay 2 (System Health).
        /// Relay 2 is turned ON when any health fault is detected, OFF when all faults clear.
        /// </summary>
        private async Task HandleSystemHealthFaultEvent(SystemHealthFaultEvent @event)
        {
            if (@event.IsNewFault && @event.Fault.IsActive)
            {
                // New health fault detected - turn ON System Health relay (Relay 2)
                _logger.LogWarning(
                    "[RELAY] Health fault detected: {FaultType} → turning ON System Health relay (Relay 2)",
                    @event.Fault.FaultType);

                await SetSystemHealthFaultRelayAsync(true);
            }
            else if (!@event.Fault.IsActive)
            {
                // Fault cleared - check if any other faults are still active
                // Only turn OFF System Health relay if ALL faults are cleared
                var activeFaultsCount = await GetActiveHealthFaultsCountAsync();

                if (activeFaultsCount == 0)
                {
                    _logger.LogInformation(
                        "[RELAY] All health faults cleared → turning OFF System Health relay (Relay 2)");

                    await SetSystemHealthFaultRelayAsync(false);
                }
                else
                {
                    _logger.LogDebug(
                        "[RELAY] Health fault {FaultType} cleared but {Count} other faults still active",
                        @event.Fault.FaultType, activeFaultsCount);
                }
            }
        }

        /// <summary>
        /// Gets the count of active health faults from SystemHealthManager.
        /// </summary>
        private async Task<int> GetActiveHealthFaultsCountAsync()
        {
            var activeFaults = await _systemHealthManager.GetActiveHealthFaultsAsync();
            return activeFaults.Count;
        }

        /// <summary>
        /// Saves current relay states to file for persistence across restarts.
        /// Uses atomic write (temp file + move) to prevent corruption on crash.
        /// </summary>
        private async Task SaveRelayStatesAsync()
        {
            var fullPath = Path.GetFullPath(RelayStatesFilePath);
            var tempPath = fullPath + ".tmp";

            try
            {
                var directory = Path.GetDirectoryName(fullPath);

                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var statesToSave = _relayStates.Values.Select(s => new
                {
                    s.RelayId,
                    s.IsOn,
                    s.ControlSource,
                    s.LastStateChange,
                    s.LastChangedBy
                }).ToList();

                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(statesToSave, options);

                // FIX: Atomic write - write to temp file first
                await File.WriteAllTextAsync(tempPath, json);

                // Move temp file to final location (atomic on same filesystem)
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }
                File.Move(tempPath, fullPath);

                _logger.LogDebug("[RELAY] Saved {Count} states to {Path}",
                    statesToSave.Count, fullPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RELAY] Error saving states to file");

                // Clean up temp file if it exists
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(cleanupEx, "[RELAY] Failed to clean up temp file {TempPath}", tempPath);
                }
            }
        }

        /// <summary>
        /// Loads relay states from file if it exists.
        /// Validates relay IDs against current configuration before restoring.
        /// </summary>
        public async Task LoadRelayStatesAsync()
        {
            try
            {
                var fullPath = Path.GetFullPath(RelayStatesFilePath);

                if (!File.Exists(fullPath))
                {
                    _logger.LogInformation(
                        "[RELAY] No states file found at {Path}, using defaults", fullPath);
                    return;
                }

                var json = await File.ReadAllTextAsync(fullPath);
                var savedStates = JsonSerializer.Deserialize<List<RelayState>>(json);

                if (savedStates == null)
                {
                    _logger.LogWarning(
                        "[RELAY] Failed to deserialize states from {Path}", fullPath);
                    return;
                }

                // FIX: respect configured relay count (1-based IDs)
                var systemConfig = await _configManager.GetSystemConfigurationAsync();
                int maxRelayId = systemConfig.NumberOfRelays;  // 1-based: max ID equals count

                int restoredCount = 0;
                int ignoredCount = 0;

                foreach (var savedState in savedStates)
                {
                    // FIX: Validate 1-based relay ID range (1 to maxRelayId)
                    if (savedState.RelayId < 1 || savedState.RelayId > maxRelayId)
                    {
                        ignoredCount++;
                        _logger.LogWarning(
                            "[RELAY] Load: Ignoring relay {RelayId} outside valid range 1-{MaxRelayId}",
                            savedState.RelayId,
                            maxRelayId);
                        continue;
                    }

                    // FIX: Don't restore ANY relay as ON on startup (except Relay 1 handled in Program.cs)
                    // Alarms are not persisted, so all alarm-related relays should start OFF
                    // They will turn ON automatically when actual alarms/conditions are detected
                    if (savedState.IsOn && savedState.RelayId != RELAY_SYSTEM_RUNNING)
                    {
                        _logger.LogWarning(
                            "[RELAY] Load: Skipping restore of relay {RelayId} as ON - all relays start OFF on startup",
                            savedState.RelayId);
                        // Keep relay OFF (default state from InitializeRelays)
                        continue;
                    }

                    if (_relayStates.TryGetValue(savedState.RelayId, out var currentState))
                    {
                        currentState.IsOn = savedState.IsOn;
                        currentState.ControlSource = savedState.ControlSource;
                        currentState.LastStateChange = savedState.LastStateChange;
                        currentState.LastChangedBy = savedState.LastChangedBy;

                        if (savedState.IsOn)
                        {
                            await SendRelayCommandAsync(savedState.RelayId, true);
                        }

                        restoredCount++;
                    }
                }

                _logger.LogInformation(
                    "[RELAY] Restored {Restored} states (ignored {Ignored}) from {Path}",
                    restoredCount,
                    ignoredCount,
                    fullPath);

                // FIX: After loading states, verify alarm-related relays match actual alarm state
                // Alarms are not persisted, so on restart there should be no active alarms
                // Turn off Common Alarm (Relay 3) and Fiber Break relays (4-11) if no alarms exist
                await VerifyAlarmRelaysOnStartupAsync();

                // Rewrite file so old/invalid relays never come back
                await SaveRelayStatesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RELAY] Error loading states from file");
            }
        }

        /// <summary>
        /// Verifies alarm-related relays match actual alarm state on startup.
        /// Turns off Common Alarm and Fiber Break relays if no active alarms exist.
        /// </summary>
        private async Task VerifyAlarmRelaysOnStartupAsync()
        {
            try
            {
                var activeAlarms = await _alarmManager.GetActiveAlarmsAsync();

                if (!activeAlarms.Any())
                {
                    _logger.LogWarning(
                        "[RELAY] Startup check: No active alarms - clearing alarm-related relays");

                    // Turn off Common Alarm relay (Relay 3) if it's ON
                    if (_relayStates.TryGetValue(RELAY_COMMON_ALARM, out var commonAlarmState) && commonAlarmState.IsOn)
                    {
                        _logger.LogWarning("[RELAY] Turning OFF Common Alarm relay (Relay 3) - no active alarms on startup");
                        await TurnRelayOffAsync(RELAY_COMMON_ALARM, RelayControlSource.Auto, "System-Startup-NoAlarms");
                    }

                    // Turn off Fiber Break relays (4-11) if they're ON
                    for (int relayId = RELAY_FIBER_BREAK_CH1; relayId <= 11; relayId++)
                    {
                        if (_relayStates.TryGetValue(relayId, out var fiberBreakState) && fiberBreakState.IsOn)
                        {
                            _logger.LogWarning("[RELAY] Turning OFF Fiber Break relay {RelayId} - no active alarms on startup", relayId);
                            await TurnRelayOffAsync(relayId, RelayControlSource.Auto, "System-Startup-NoAlarms");
                        }
                    }

                    // Also clear heartbeat alarm
                    await _rs232Manager.SetHeartbeatAlarmAsync(false);
                }
                else
                {
                    _logger.LogInformation(
                        "[RELAY] Startup check: {Count} active alarms found - keeping alarm relays as-is",
                        activeAlarms.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RELAY] Error verifying alarm relays on startup");
            }
        }

        /// <summary>
        /// Sets the System Running relay (Relay 1) ON or OFF.
        /// Relay 1 should be ON whenever the system is running.
        /// </summary>
        public async Task SetSystemRunningRelayAsync(bool isRunning)
        {
            _logger.LogInformation("[RELAY] Setting System Running relay (Relay 1) to {State}", isRunning ? "ON" : "OFF");

            if (isRunning)
            {
                await TurnRelayOnAsync(RELAY_SYSTEM_RUNNING, RelayControlSource.Auto, "System-Startup");
            }
            else
            {
                await TurnRelayOffAsync(RELAY_SYSTEM_RUNNING, RelayControlSource.Auto, "System-Shutdown");
            }
        }

        /// <summary>
        /// Sets the System Health Fault relay (Relay 2) ON or OFF.
        /// Relay 2 should be ON when system health issues/faults are detected.
        /// </summary>
        public async Task SetSystemHealthFaultRelayAsync(bool hasFault)
        {
            _logger.LogInformation("[RELAY] Setting System Health relay (Relay 2) to {State}", hasFault ? "ON" : "OFF");

            if (hasFault)
            {
                await TurnRelayOnAsync(RELAY_SYSTEM_HEALTH, RelayControlSource.Auto, "System-Health-Fault");
            }
            else
            {
                await TurnRelayOffAsync(RELAY_SYSTEM_HEALTH, RelayControlSource.Auto, "System-Health-OK");
            }
        }

        /// <summary>
        /// Sets the Common Alarm relay (Relay 3) ON or OFF.
        /// Relay 3 should be ON when ANY alarm is active in the system.
        /// </summary>
        public async Task SetCommonAlarmRelayAsync(bool hasAlarm)
        {
            _logger.LogInformation("[RELAY] Setting Common Alarm relay (Relay 3) to {State}", hasAlarm ? "ON" : "OFF");

            if (hasAlarm)
            {
                await TurnRelayOnAsync(RELAY_COMMON_ALARM, RelayControlSource.Auto, "System-Alarm-Active");
            }
            else
            {
                await TurnRelayOffAsync(RELAY_COMMON_ALARM, RelayControlSource.Auto, "System-Alarm-Cleared");
            }
        }

        public async Task<bool> TurnRelayOnAsync(int relayId, RelayControlSource source, string user)
        {
            if (!_relayStates.TryGetValue(relayId, out var state))
            {
                _logger.LogWarning("[RELAY] ON failed: relay {RelayId} not found in dictionary", relayId);

                // FIX Bug #8: Publish failure event so callers/UI can be notified
                await _eventAggregator.PublishAsync(new RelayCommandFailedEvent(
                    relayId,
                    $"RELAY {relayId} ON",
                    $"Relay {relayId} not found - may not be initialized or outside valid range",
                    DateTime.UtcNow));

                return false;
            }

            bool commandSuccess = await SendRelayCommandAsync(relayId, true);

            if (!commandSuccess)
            {
                await _eventAggregator.PublishAsync(new RelayCommandFailedEvent(
                    relayId,
                    $"RELAY {relayId} ON",
                    "Failed to send RS232 command",
                    DateTime.UtcNow));

                return false;
            }

            state.IsOn = true;
            state.ControlSource = source;
            state.LastStateChange = DateTime.UtcNow;
            state.LastChangedBy = user;

            await _eventAggregator.PublishAsync(new RelayStateChangedEvent(
                relayId,
                true,
                source,
                user,
                DateTime.UtcNow));

            // NOTE: Heartbeat alarm is now managed explicitly by HandleAlarmTriggeredEvent
            // Not set here because Relay 1 (System Running) and Relay 2 (System Health)
            // turning ON should not set the heartbeat alarm - only actual alarms should.

            await SaveRelayStatesAsync();
            return true;
        }

        public async Task<bool> TurnRelayOffAsync(int relayId, RelayControlSource source, string user)
        {
            if (!_relayStates.TryGetValue(relayId, out var state))
            {
                _logger.LogWarning("[RELAY] OFF failed: relay {RelayId} not found in dictionary", relayId);

                // FIX Bug #8: Publish failure event so callers/UI can be notified
                await _eventAggregator.PublishAsync(new RelayCommandFailedEvent(
                    relayId,
                    $"RELAY {relayId} OFF",
                    $"Relay {relayId} not found - may not be initialized or outside valid range",
                    DateTime.UtcNow));

                return false;
            }

            bool commandSuccess = await SendRelayCommandAsync(relayId, false);

            if (!commandSuccess)
            {
                await _eventAggregator.PublishAsync(new RelayCommandFailedEvent(
                    relayId,
                    $"RELAY {relayId} OFF",
                    "Failed to send RS232 command",
                    DateTime.UtcNow));

                return false;
            }

            state.IsOn = false;
            state.ControlSource = source;
            state.LastStateChange = DateTime.UtcNow;
            state.LastChangedBy = user;

            await _eventAggregator.PublishAsync(new RelayStateChangedEvent(
                relayId,
                false,
                source,
                user,
                DateTime.UtcNow));

            await SaveRelayStatesAsync();
            return true;
        }

        public Task<RelayState?> GetRelayStateAsync(int relayId)
        {
            // FIX Bug #9: Add logging when relay not found
            if (_relayStates.TryGetValue(relayId, out var state))
            {
                return Task.FromResult<RelayState?>(state);
            }

            _logger.LogDebug(
                "[RELAY] GetState: relay {RelayId} not found (initialized: {Count})",
                relayId,
                _relayStates.Count);

            return Task.FromResult<RelayState?>(null);
        }

        /// <summary>
        /// Gets a thread-safe snapshot of all relay states.
        /// </summary>
        public Task<List<RelayState>> GetAllRelayStatesAsync()
        {
            // FIX: Create a proper snapshot to avoid issues during concurrent modifications
            // ConcurrentDictionary.Values.ToList() is safe but we log for debugging
            var snapshot = _relayStates.Values.ToList();
            _logger.LogDebug("[RELAY] GetAllStates returning {Count} relays", snapshot.Count);
            return Task.FromResult(snapshot);
        }



        private async Task HandleAlarmTriggeredEvent(AlarmTriggeredEvent @event)
        {
            _logger.LogInformation(
                "[RELAY] Alarm triggered → CH:{ChannelId}, Z:{ZoneId}, Type:{Type}",
                @event.ChannelId, @event.ZoneId, @event.Type);

            int relayId;

            // Handle channel-level alarms (ZoneId = 0) using reserved relays
            if (@event.ZoneId == 0)
            {
                // Channel-level alarm - use reserved relays
                relayId = GetReservedRelayForChannelAlarm(@event.ChannelId, @event.Type);

                if (relayId <= 0)
                {
                    _logger.LogDebug(
                        "[RELAY] No reserved relay mapped for channel alarm → CH:{ChannelId}, Type:{Type} - but still setting Common Alarm",
                        @event.ChannelId, @event.Type);

                    // Even if no specific relay is mapped, we should still:
                    // 1. Set heartbeat alarm
                    // 2. Turn on Common Alarm relay (Relay 3)
                    await _rs232Manager.SetHeartbeatAlarmAsync(true);
                    await SetCommonAlarmRelayAsync(true);
                    return;
                }

                _logger.LogInformation(
                    "[RELAY] Channel-level alarm → using reserved relay {RelayId} for CH:{ChannelId}, Type:{Type}",
                    relayId, @event.ChannelId, @event.Type);
            }
            else
            {
                // Zone-level alarm - get assigned relay from zone config
                var zone = await _configManager.GetZoneConfigAsync(
                    @event.ChannelId, @event.ZoneId);

                if (zone == null)
                {
                    _logger.LogWarning(
                        "[RELAY] Zone config not found → CH:{ChannelId}, Z:{ZoneId}",
                        @event.ChannelId, @event.ZoneId);
                    return;
                }

                // SIMPLIFIED: Relay triggers on ANY enabled alarm for this zone
                if (!zone.AssignedRelayId.HasValue)
                {
                    _logger.LogDebug(
                        "[RELAY] Zone {ZoneId} has no assigned relay - but still setting Common Alarm",
                        zone.ZoneId);

                    // Even if zone has no assigned relay, we should still:
                    // 1. Set heartbeat alarm
                    // 2. Turn on Common Alarm relay (Relay 3)
                    await _rs232Manager.SetHeartbeatAlarmAsync(true);
                    await SetCommonAlarmRelayAsync(true);
                    return;
                }

                relayId = zone.AssignedRelayId.Value;
            }

            // Validate relay exists
            if (!_relayStates.TryGetValue(relayId, out var relayState))
            {
                _logger.LogWarning(
                    "[RELAY] Relay {RelayId} not initialized - cannot trigger",
                    relayId);
                return;
            }

            // Respect manual override
            if (relayState.ControlSource == RelayControlSource.Manual)
            {
                _logger.LogWarning(
                    "[RELAY] Relay {RelayId} is MANUAL → skipping auto ON",
                    relayId);
                return;
            }

            // FIX: Skip if relay already ON (avoid redundant RS232 commands)
            if (relayState.IsOn)
            {
                _logger.LogDebug(
                    "[RELAY] Relay {RelayId} already ON → skipping duplicate",
                    relayId);
                // Still set heartbeat for safety (alarm is active)
                await _rs232Manager.SetHeartbeatAlarmAsync(true);
                return;
            }

            _logger.LogWarning(
                "[RELAY] ON relay {RelayId} for CH:{ChannelId}, Z:{ZoneId} (alarm: {Type})",
                relayId, @event.ChannelId, @event.ZoneId, @event.Type);

            // FIX: Set heartbeat AFTER validation, BEFORE relay command
            await _rs232Manager.SetHeartbeatAlarmAsync(true);

            // Turn on Common Alarm relay (Relay 3) for any alarm in system
            await SetCommonAlarmRelayAsync(true);

            await TurnRelayOnAsync(
                relayId,
                RelayControlSource.Auto,
                "System");
        }

        /// <summary>
        /// Gets the reserved relay ID for a channel-level alarm.
        /// FiberBreak alarms use relays 4-11 (Relay 4 = CH1, Relay 5 = CH2, ..., Relay 11 = CH8)
        /// </summary>
        private int GetReservedRelayForChannelAlarm(int channelId, AlarmType alarmType)
        {
            if (alarmType == AlarmType.FiberBreak)
            {
                // FiberBreak relays: CH1 = Relay 4, CH2 = Relay 5, ..., CH8 = Relay 11
                int relayId = RELAY_FIBER_BREAK_CH1 + (channelId - 1);

                // Validate relay is within reserved range (4-11 for 8 channels)
                if (relayId >= RELAY_FIBER_BREAK_CH1 && relayId <= 11)
                {
                    return relayId;
                }

                _logger.LogWarning(
                    "[RELAY] FiberBreak relay calculation out of range: CH:{ChannelId} → Relay {RelayId}",
                    channelId, relayId);
            }

            // No reserved relay for this alarm type
            return 0;
        }

        /// <summary>
        /// Handles channel-level alarm clear for reserved relays.
        /// Turns off the reserved relay if no other FiberBreak alarms are active on that channel.
        /// </summary>
        private async Task HandleChannelAlarmClear(int channelId, AlarmType alarmType)
        {
            int relayId = GetReservedRelayForChannelAlarm(channelId, alarmType);

            if (relayId <= 0)
            {
                _logger.LogDebug(
                    "[RELAY] No reserved relay mapped for channel alarm clear → CH:{ChannelId}, Type:{Type}",
                    channelId, alarmType);
                return;
            }

            // Check if relay exists and is ON
            if (!_relayStates.TryGetValue(relayId, out var relayState))
            {
                _logger.LogWarning(
                    "[RELAY] Reserved relay {RelayId} not initialized - cannot clear",
                    relayId);
                return;
            }

            if (!relayState.IsOn)
            {
                _logger.LogDebug(
                    "[RELAY] Reserved relay {RelayId} already OFF - skipping",
                    relayId);
                return;
            }

            // Respect manual override
            if (relayState.ControlSource == RelayControlSource.Manual)
            {
                _logger.LogWarning(
                    "[RELAY] Reserved relay {RelayId} is MANUAL → skipping auto OFF",
                    relayId);
                return;
            }

            _logger.LogInformation(
                "[RELAY] Turning OFF reserved relay {RelayId} for CH:{ChannelId} (alarm cleared: {Type})",
                relayId, channelId, alarmType);

            await TurnRelayOffAsync(relayId, RelayControlSource.Auto, "System-FiberBreak-Cleared");
        }



        /// <summary>
        /// Sends a relay ON/OFF command via RS232.
        /// </summary>
        /// <param name="relayId">1-based relay ID (1-255)</param>
        /// <param name="turnOn">True to turn relay ON, false to turn OFF</param>
        /// <returns>True if command was sent successfully</returns>
        private async Task<bool> SendRelayCommandAsync(int relayId, bool turnOn)
        {
            // FIX: Validate relay ID is within valid 1-based range before casting
            // Relays are 1-based (1 to 255), relay ID 0 is invalid
            if (relayId < 1 || relayId > 255)
            {
                _logger.LogError(
                    "[RELAY] Invalid ID {RelayId} (valid: 1-255, 1-based). Command not sent.",
                    relayId);
                return false;
            }

//#if DEBUG
//            // In DEBUG mode, skip RS232 commands (no hardware connected)
//            _logger.LogDebug(
//                "[RELAY] DEBUG MODE: Simulating RS232 command for relay {RelayId} → {State}",
//                relayId, turnOn ? "ON" : "OFF");
//            await Task.CompletedTask;
//            return true;
//#else
            try
            {
                _logger.LogDebug(
                    "[RELAY] Sending RS232: ID={RelayId}, State={State}",
                    relayId, turnOn ? "ON" : "OFF");

                await _rs232Manager.SendRelayCommandAsync((byte)relayId, turnOn);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RELAY] RS232 send failed for relay {RelayId}", relayId);
                return false;
            }
//#endif
        }
    }
}
