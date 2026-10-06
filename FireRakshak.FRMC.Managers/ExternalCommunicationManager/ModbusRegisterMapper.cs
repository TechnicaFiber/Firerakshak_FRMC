using FireRakshak.FRMC.Core.Enums;
using FireRakshak.FRMC.Core.Models;
using FireRakshak.FRMC.Managers.ChannelsManager;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FireRakshak.FRMC.Managers.ExternalCommunicationManager
{
    /// <summary>
    /// Maps internal system data to Modbus register addresses.
    /// Provides dynamic zone-to-register mapping for all configured zones.
    /// </summary>
    public class ModbusRegisterMapper
    {
        private readonly ILogger<ModbusRegisterMapper> _logger;
        private readonly IChannelsManager _channelsManager;
        private Dictionary<(int ChannelId, int ZoneId), int> _zoneToRegisterMap;

        // Dynamic address tracking for variable-length sections (enabled-only mapping)
        private int _enabledChannelCount;
        private int _enabledZoneCount;
        private int _fiberAlarmCoilStart;
        private int _zoneAlarmCoilStart;
        private int _fiberBreakRegisterStart;
        private int _zoneDataRegisterStart;
        private int _channelEventRegisterStart;
        private int _zoneThresholdRegisterStart;

        // Channel-to-index mapping for enabled channels (ChannelId -> 0-based index)
        private Dictionary<int, int> _channelToIndexMap = new Dictionary<int, int>();

        public ModbusRegisterMapper(ILogger<ModbusRegisterMapper> logger, IChannelsManager channelsManager)
        {
            _logger = logger;
            _channelsManager = channelsManager;
            _zoneToRegisterMap = new Dictionary<(int, int), int>();
        }

        /// <summary>
        /// Gets only enabled channels ordered by ChannelId.
        /// </summary>
        private List<Channel> GetEnabledChannels(List<Channel> channels)
        {
            return channels
                .Where(c => c.Configuration.IsEnabled)
                .OrderBy(c => c.Configuration.ChannelId)
                .ToList();
        }

        /// <summary>
        /// Gets only enabled zones from a channel ordered by ZoneId.
        /// </summary>
        private List<Zone> GetEnabledZones(Channel channel)
        {
            return channel.Zones
                .Where(z => z.Configuration.Enabled)
                .OrderBy(z => z.Configuration.ZoneId)
                .ToList();
        }

        /// <summary>
        /// Builds the mapping from (ChannelId, ZoneId) to base register address.
        /// Only maps enabled channels and zones. Calculates all dynamic address offsets.
        /// </summary>
        public void BuildZoneMapping(List<Channel> channels)
        {
            _zoneToRegisterMap.Clear();
            _channelToIndexMap.Clear();

            _logger.LogInformation("Building zone-to-register mapping (enabled only)...");

            var enabledChannels = GetEnabledChannels(channels);
            _enabledChannelCount = enabledChannels.Count;

            // Build channel-to-index mapping
            int channelIndex = 0;
            foreach (var channel in enabledChannels)
            {
                _channelToIndexMap[channel.Configuration.ChannelId] = channelIndex;
                channelIndex++;
            }

            // Calculate dynamic address offsets
            // Base registers: 0-4 (5 registers: Reset, TimeValid, Time1-3)
            _fiberBreakRegisterStart = 5; // Fiber break positions start at register 5
            _zoneDataRegisterStart = _fiberBreakRegisterStart + _enabledChannelCount; // After fiber break positions

            // Coil addresses
            _fiberAlarmCoilStart = 2 + _enabledChannelCount; // After fiber broken coils (2 to 2+M-1)
            _zoneAlarmCoilStart = _fiberAlarmCoilStart + _enabledChannelCount; // After fiber alarm coils

            // Map zones to registers (starting after fiber break positions)
            int registerIndex = _zoneDataRegisterStart;
            _enabledZoneCount = 0;

            foreach (var channel in enabledChannels)
            {
                foreach (var zone in GetEnabledZones(channel))
                {
                    _zoneToRegisterMap[(channel.Configuration.ChannelId, zone.Configuration.ZoneId)] = registerIndex;
                    _logger.LogDebug("Mapped Ch{ChId}/Zone{ZId} -> Register {Reg}",
                        channel.Configuration.ChannelId, zone.Configuration.ZoneId, registerIndex);
                    registerIndex += 5; // Each zone uses 5 registers
                    _enabledZoneCount++;
                }
            }

            // Calculate remaining address offsets
            _channelEventRegisterStart = _zoneDataRegisterStart + (_enabledZoneCount * 5);
            _zoneThresholdRegisterStart = _channelEventRegisterStart + (_enabledChannelCount * 3);

            _logger.LogInformation("Zone mapping complete: {ZoneCount} enabled zones, {ChannelCount} enabled channels mapped",
                _enabledZoneCount, _enabledChannelCount);
            _logger.LogDebug("Dynamic addresses - FiberBreak:{FB}, ZoneData:{ZD}, ChannelEvent:{CE}, ZoneThreshold:{ZT}",
                _fiberBreakRegisterStart, _zoneDataRegisterStart, _channelEventRegisterStart, _zoneThresholdRegisterStart);
            _logger.LogDebug("Dynamic coil addresses - FiberAlarm:{FA}, ZoneAlarm:{ZA}",
                _fiberAlarmCoilStart, _zoneAlarmCoilStart);
        }

        /// <summary>
        /// Gets the base register address for a specific zone.
        /// </summary>
        /// <returns>Base register address, or -1 if zone not found</returns>
        public int GetZoneBaseRegister(int channelId, int zoneId)
        {
            if (_zoneToRegisterMap.TryGetValue((channelId, zoneId), out int baseAddr))
                return baseAddr;
            return -1;
        }

        /// <summary>
        /// Initialize coil values from actual system, channel, and zone states.
        /// Only maps enabled channels and zones for dynamic addressing.
        /// </summary>
        /// <param name="coils">The coil array to initialize</param>
        /// <param name="channels">The configured channels with zones</param>
        public void InitializeCoils(bool[] coils, List<Channel> channels)
        {
            _logger.LogInformation("Initializing {Count} coils from live states (enabled only)...", coils.Length);

            // System Status (0-1)
            // Coil 0 (System Health) is managed by UpdateSystemHealthStatusCoil - true=FAULT, false=NORMAL
            coils[1] = true; // Running Status = Running (always true when server is active)

            var enabledChannels = GetEnabledChannels(channels);

            // Fiber Broken flags - dynamic for enabled channels only (coils 2 to 2+M-1)
            int coilIndex = 2;
            foreach (var channel in enabledChannels)
            {
                if (coilIndex < coils.Length)
                {
                    coils[coilIndex] = channel.State.FiberBroken;
                }
                coilIndex++;
            }

            // Fiber Alarm flags - dynamic for enabled channels only (coils 2+M to 2+2M-1)
            coilIndex = _fiberAlarmCoilStart;
            foreach (var channel in enabledChannels)
            {
                if (coilIndex < coils.Length)
                {
                    var enabledZones = GetEnabledZones(channel);
                    coils[coilIndex] = enabledZones.Any(z => (z.State.PreAlarm || z.State.MaxAlarm || z.State.RorAlarm || z.State.DeviationAlarm));
                }
                coilIndex++;
            }

            // Zone alarms - dynamic for enabled zones only (coils 2+2M onwards)
            coilIndex = _zoneAlarmCoilStart;
            foreach (var channel in enabledChannels)
            {
                foreach (var zone in GetEnabledZones(channel))
                {
                    if (coilIndex + 3 < coils.Length)
                    {
                        coils[coilIndex + 0] = zone.State.PreAlarm;
                        coils[coilIndex + 1] = zone.State.MaxAlarm;
                        coils[coilIndex + 2] = zone.State.RorAlarm;
                        coils[coilIndex + 3] = zone.State.DeviationAlarm;

                        _logger.LogDebug("Initialized coils for Ch{ChId}/Zone{ZId} at coil {CoilIdx}: Pre={Pre}, Max={Max}, RoR={Ror}, Dev={Dev}",
                            channel.Configuration.ChannelId, zone.Configuration.ZoneId, coilIndex,
                            zone.State.PreAlarm, zone.State.MaxAlarm, zone.State.RorAlarm, zone.State.DeviationAlarm);
                    }
                    coilIndex += 4;
                }
            }

            _logger.LogInformation("Coils initialized: System(2) + FiberBroken({M}) + FiberAlarm({M}) + ZoneAlarms({ZoneCoils})",
                _enabledChannelCount, _enabledChannelCount, coilIndex - _zoneAlarmCoilStart);
        }

        /// <summary>
        /// Initialize holding register values from actual zone states.
        /// Only initializes for enabled channels and zones using dynamic addressing.
        /// </summary>
        public async Task InitializeRegisters(ushort[] holdingRegisters, List<Channel> channels)
        {
            _logger.LogInformation("Initializing {Count} holding registers from live zone states (enabled only)...", holdingRegisters.Length);

            // Build zone mapping first (also calculates dynamic addresses)
            BuildZoneMapping(channels);

            var enabledChannels = GetEnabledChannels(channels);

            // Base registers 0-4: System control and time
            holdingRegisters[0] = 0;       // Reset Control (0=idle)
            holdingRegisters[1] = 1;       // Time Validity Flag

            var now = DateTime.Now;
            holdingRegisters[2] = (ushort)((now.Year % 100) << 8 | now.Month);     // YYMM
            holdingRegisters[3] = (ushort)(now.Day << 8 | now.Hour);               // DDHH
            holdingRegisters[4] = (ushort)(now.Minute << 8 | now.Second);          // MMSS

            // Fiber Broken Positions - dynamic for enabled channels only (registers 5 to 5+M-1)
            int registerIndex = _fiberBreakRegisterStart;
            foreach (var channel in enabledChannels)
            {
                if (registerIndex < holdingRegisters.Length)
                {
                    holdingRegisters[registerIndex] = channel.State.FiberBreakPosition.HasValue
                        ? (ushort)channel.State.FiberBreakPosition.Value
                        : (ushort)0;
                }
                registerIndex++;
            }

            // Zone realtime values - only for enabled zones (dynamic addresses from BuildZoneMapping)
            int zonesInitialized = 0;
            foreach (var channel in enabledChannels)
            {
                foreach (var zone in GetEnabledZones(channel))
                {
                    int baseAddr = GetZoneBaseRegister(channel.Configuration.ChannelId, zone.Configuration.ZoneId);
                    if (baseAddr < 0) continue;

                    // Get current zone state from ChannelsManager
                    var state = await _channelsManager.GetZoneStateAsync(zone.Configuration.ZoneId);

                    holdingRegisters[baseAddr + 0] = ConvertTemperatureToModbus(state.MaxTemperature);
                    holdingRegisters[baseAddr + 1] = ConvertRorToModbus(state.RateOfRise);
                    holdingRegisters[baseAddr + 2] = ConvertTemperatureToModbus(state.Deviation);
                    holdingRegisters[baseAddr + 3] = ConvertTemperatureToModbus(state.AverageTemperature);
                    holdingRegisters[baseAddr + 4] = ConvertPositionToModbus(state.MaxTempPosition, channel.Configuration.ChannelId, zone.Configuration.ZoneId);

                    zonesInitialized++;

                    _logger.LogDebug("Initialized Ch{ChId}/Zone{ZId} at reg {Reg}: Max={Max}, RoR={RoR}, Dev={Dev}, Avg={Avg}",
                        channel.Configuration.ChannelId, zone.Configuration.ZoneId, baseAddr,
                        state.MaxTemperature, state.RateOfRise, state.Deviation, state.AverageTemperature);
                }
            }

            _logger.LogInformation("Zone registers initialized: {Count} enabled zones", zonesInitialized);

            // Channel Event Times - only for enabled channels (3 registers per channel × M channels)
            int channelIndex = 0;
            foreach (var channel in enabledChannels)
            {
                int baseAddr = _channelEventRegisterStart + (channelIndex * 3);
                if (baseAddr + 2 < holdingRegisters.Length)
                {
                    if (channel.State.LastAlarmTime.HasValue)
                    {
                        var alarmTime = channel.State.LastAlarmTime.Value;
                        holdingRegisters[baseAddr + 0] = (ushort)((alarmTime.Year % 100) << 8 | alarmTime.Month);
                        holdingRegisters[baseAddr + 1] = (ushort)(alarmTime.Day << 8 | alarmTime.Hour);
                        holdingRegisters[baseAddr + 2] = (ushort)(alarmTime.Minute << 8 | alarmTime.Second);
                        _logger.LogDebug("Channel {ChId} event time initialized: {Time}",
                            channel.Configuration.ChannelId, alarmTime);
                    }
                    else
                    {
                        // No alarm - clear registers
                        holdingRegisters[baseAddr + 0] = 0;
                        holdingRegisters[baseAddr + 1] = 0;
                        holdingRegisters[baseAddr + 2] = 0;
                        _logger.LogDebug("Channel {ChId} event time cleared (no alarms)",
                            channel.Configuration.ChannelId);
                    }
                }
                channelIndex++;
            }

            // Zone alarm breach values and positions - only for enabled zones (8 registers per zone)
            int thresholdIndex = 0;
            foreach (var channel in enabledChannels)
            {
                foreach (var zone in GetEnabledZones(channel))
                {
                    int baseAddr = _zoneThresholdRegisterStart + (thresholdIndex * 8);
                    if (baseAddr + 7 < holdingRegisters.Length)
                    {
                        var state = await _channelsManager.GetZoneStateAsync(zone.Configuration.ZoneId);

                        // Pre-Alarm breach data
                        holdingRegisters[baseAddr + 0] = state.PreAlarmBreachValue.HasValue
                            ? ConvertTemperatureToModbus(state.PreAlarmBreachValue.Value) : (ushort)0;
                        holdingRegisters[baseAddr + 1] = state.PreAlarmBreachPosition.HasValue
                            ? ConvertPositionToModbus(state.PreAlarmBreachPosition.Value, channel.Configuration.ChannelId, zone.Configuration.ZoneId) : (ushort)0;

                        // Max Alarm breach data
                        holdingRegisters[baseAddr + 2] = state.MaxAlarmBreachValue.HasValue
                            ? ConvertTemperatureToModbus(state.MaxAlarmBreachValue.Value) : (ushort)0;
                        holdingRegisters[baseAddr + 3] = state.MaxAlarmBreachPosition.HasValue
                            ? ConvertPositionToModbus(state.MaxAlarmBreachPosition.Value, channel.Configuration.ChannelId, zone.Configuration.ZoneId) : (ushort)0;

                        // Rate of Rise breach data
                        holdingRegisters[baseAddr + 4] = state.RorAlarmBreachValue.HasValue
                            ? ConvertRorToModbus(state.RorAlarmBreachValue.Value) : (ushort)0;
                        holdingRegisters[baseAddr + 5] = state.RorAlarmBreachPosition.HasValue
                            ? ConvertPositionToModbus(state.RorAlarmBreachPosition.Value, channel.Configuration.ChannelId, zone.Configuration.ZoneId) : (ushort)0;

                        // Deviation breach data
                        holdingRegisters[baseAddr + 6] = state.DeviationAlarmBreachValue.HasValue
                            ? ConvertTemperatureToModbus(state.DeviationAlarmBreachValue.Value) : (ushort)0;
                        holdingRegisters[baseAddr + 7] = state.DeviationAlarmBreachPosition.HasValue
                            ? ConvertPositionToModbus(state.DeviationAlarmBreachPosition.Value, channel.Configuration.ChannelId, zone.Configuration.ZoneId) : (ushort)0;

                        _logger.LogDebug("Initialized alarm breach registers for Ch{ChId}/Zone{ZId} at addr {Addr}",
                            channel.Configuration.ChannelId, zone.Configuration.ZoneId, baseAddr);
                    }
                    thresholdIndex++;
                }
            }

            _logger.LogInformation("Holding registers initialization complete");
        }

        /// <summary>
        /// Updates zone registers with new real-time values.
        /// Called when ZoneStateUpdatedEvent is received.
        ///
        /// IMPORTANT: Values are converted for Modbus protocol:
        /// - Temperatures: Multiplied by 10 and rounded (e.g., 45.7°C → 457)
        /// - Negative values: Stored using two's complement in signed int16 range
        /// - Positions: Stored as-is in meters (max 65535m for ushort)
        /// </summary>
        public void UpdateZoneRegisters(ushort[] holdingRegisters, int channelId, int zoneId, ZoneState state)
        {
            int baseAddr = GetZoneBaseRegister(channelId, zoneId);
            if (baseAddr < 0)
            {
                _logger.LogWarning("Cannot update registers for unknown zone: Ch{ChId}/Zone{ZId}", channelId, zoneId);
                return;
            }

            // ✅ FIX: Support negative temperatures using signed int16 representation
            // Convert double to signed int16 (multiplied by 10 for one decimal precision)
            // Modbus clients should interpret as signed int16 to get correct negative values
            holdingRegisters[baseAddr + 0] = ConvertTemperatureToModbus(state.MaxTemperature);
            holdingRegisters[baseAddr + 1] = ConvertRorToModbus(state.RateOfRise);
            holdingRegisters[baseAddr + 2] = ConvertTemperatureToModbus(state.Deviation);
            holdingRegisters[baseAddr + 3] = ConvertTemperatureToModbus(state.AverageTemperature);

            // ✅ FIX: Clamp position to valid ushort range with warning for overflow
            holdingRegisters[baseAddr + 4] = ConvertPositionToModbus(state.MaxTempPosition, channelId, zoneId);

            _logger.LogDebug("Updated Modbus registers for Ch{ChId}/Zone{ZId} at addr {Addr}: Max={Max}°C, RoR={RoR}, Dev={Dev}, Avg={Avg}°C, Pos={Pos}m",
                channelId, zoneId, baseAddr,
                state.MaxTemperature, state.RateOfRise, state.Deviation, state.AverageTemperature, state.MaxTempPosition);
        }

        /// <summary>
        /// Converts temperature value to Modbus format (signed int16, multiplied by 10).
        /// Supports negative temperatures using two's complement representation.
        /// Range: -3276.8°C to +3276.7°C
        /// </summary>
        private ushort ConvertTemperatureToModbus(double temperature)
        {
            // Multiply by 10 and round to preserve one decimal place
            double scaled = Math.Round(temperature * 10, 0);

            // Clamp to signed int16 range (-32768 to 32767)
            int clamped = (int)Math.Max(-32768, Math.Min(32767, scaled));

            // Convert to ushort using unchecked cast (preserves two's complement for negatives)
            return unchecked((ushort)clamped);
        }
        private ushort ConvertRorToModbus(double ror)
        {
            double scaled = Math.Round(Math.Max(0, ror) * 10, 0);
            int clamped = (int)Math.Min(5000, scaled);
            return (ushort)clamped;
        }
        /// <summary>
        /// Converts position value to Modbus format (ushort).
        /// Logs warning if position exceeds ushort range (65535 meters).
        /// </summary>
        private ushort ConvertPositionToModbus(double position, int channelId, int zoneId)
        {
            // Ensure non-negative
            if (position < 0)
            {
                _logger.LogWarning("Negative position {Pos}m detected for Ch{ChId}/Zone{ZId}, clamping to 0",
                    position, channelId, zoneId);
                return 0;
            }

            // Check for overflow (position > 65535 meters = 65.5km)
            if (position > 65535)
            {
                _logger.LogWarning("Position {Pos}m exceeds ushort range for Ch{ChId}/Zone{ZId}, clamping to 65535m",
                    position, channelId, zoneId);
                return 65535;
            }

            return (ushort)Math.Round(position, 0);
        }

        /// <summary>
        /// Updates channel event time registers.
        /// Called when alarms are triggered or cleared for a channel.
        /// Uses dynamic addressing based on enabled channels only.
        /// </summary>
        /// <param name="holdingRegisters">The holding register array</param>
        /// <param name="channelId">The channel ID</param>
        /// <param name="lastAlarmTime">The last alarm timestamp, or null to clear</param>
        public void UpdateChannelEventRegisters(ushort[] holdingRegisters, int channelId, DateTime? lastAlarmTime)
        {
            // Look up the channel's index in enabled channels
            if (!_channelToIndexMap.TryGetValue(channelId, out int channelIndex))
            {
                _logger.LogDebug("Channel {ChId} not in enabled channel map, skipping event register update", channelId);
                return;
            }

            int baseAddr = _channelEventRegisterStart + (channelIndex * 3);

            if (baseAddr + 2 >= holdingRegisters.Length)
            {
                _logger.LogWarning("Channel event registers out of bounds for Ch{ChId}", channelId);
                return;
            }

            if (lastAlarmTime.HasValue)
            {
                var alarmTime = lastAlarmTime.Value;
                holdingRegisters[baseAddr + 0] = (ushort)((alarmTime.Year % 100) << 8 | alarmTime.Month);
                holdingRegisters[baseAddr + 1] = (ushort)(alarmTime.Day << 8 | alarmTime.Hour);
                holdingRegisters[baseAddr + 2] = (ushort)(alarmTime.Minute << 8 | alarmTime.Second);
                _logger.LogDebug("Updated channel {ChId} event time at addr {Addr}: {Time}", channelId, baseAddr, alarmTime);
            }
            else
            {
                // Clear event time registers
                holdingRegisters[baseAddr + 0] = 0;
                holdingRegisters[baseAddr + 1] = 0;
                holdingRegisters[baseAddr + 2] = 0;
                _logger.LogDebug("Cleared channel {ChId} event time (no alarms)", channelId);
            }
        }

        /// <summary>
        /// Updates zone alarm breach registers with current breach values and positions.
        /// Called when ZoneStateUpdatedEvent is received with updated alarm breach data.
        /// Uses dynamic addressing based on enabled zones only.
        /// </summary>
        /// <param name="holdingRegisters">The holding register array</param>
        /// <param name="channelId">The channel ID</param>
        /// <param name="zoneId">The zone ID</param>
        /// <param name="state">The zone state containing breach data</param>
        public void UpdateZoneAlarmRegisters(ushort[] holdingRegisters, int channelId, int zoneId, ZoneState state)
        {
            // Find the zone's index in the ordered enabled zone list
            int zoneIndex = 0;
            bool found = false;

            foreach (var kvp in _zoneToRegisterMap.OrderBy(k => k.Key.ChannelId).ThenBy(k => k.Key.ZoneId))
            {
                if (kvp.Key.ChannelId == channelId && kvp.Key.ZoneId == zoneId)
                {
                    found = true;
                    break;
                }
                zoneIndex++;
            }

            if (!found)
            {
                _logger.LogDebug("Zone Ch{ChId}/Zone{ZId} not in enabled zone map, skipping alarm register update", channelId, zoneId);
                return;
            }

            // Use dynamic threshold register start address
            int baseAddr = _zoneThresholdRegisterStart + (zoneIndex * 8);

            if (baseAddr + 7 >= holdingRegisters.Length)
            {
                _logger.LogWarning("Alarm registers out of bounds for Ch{ChId}/Zone{ZId}", channelId, zoneId);
                return;
            }

            // Pre-Alarm breach data
            holdingRegisters[baseAddr + 0] = state.PreAlarmBreachValue.HasValue
                ? ConvertTemperatureToModbus(state.PreAlarmBreachValue.Value) : (ushort)0;
            holdingRegisters[baseAddr + 1] = state.PreAlarmBreachPosition.HasValue
                ? ConvertPositionToModbus(state.PreAlarmBreachPosition.Value, channelId, zoneId) : (ushort)0;

            // Max Alarm breach data
            holdingRegisters[baseAddr + 2] = state.MaxAlarmBreachValue.HasValue
                ? ConvertTemperatureToModbus(state.MaxAlarmBreachValue.Value) : (ushort)0;
            holdingRegisters[baseAddr + 3] = state.MaxAlarmBreachPosition.HasValue
                ? ConvertPositionToModbus(state.MaxAlarmBreachPosition.Value, channelId, zoneId) : (ushort)0;

            // Rate of Rise breach data
            holdingRegisters[baseAddr + 4] = state.RorAlarmBreachValue.HasValue
                ? ConvertRorToModbus(state.RorAlarmBreachValue.Value) : (ushort)0;
            holdingRegisters[baseAddr + 5] = state.RorAlarmBreachPosition.HasValue
                ? ConvertPositionToModbus(state.RorAlarmBreachPosition.Value, channelId, zoneId) : (ushort)0;

            // Deviation breach data
            holdingRegisters[baseAddr + 6] = state.DeviationAlarmBreachValue.HasValue
                ? ConvertTemperatureToModbus(state.DeviationAlarmBreachValue.Value) : (ushort)0;
            holdingRegisters[baseAddr + 7] = state.DeviationAlarmBreachPosition.HasValue
                ? ConvertPositionToModbus(state.DeviationAlarmBreachPosition.Value, channelId, zoneId) : (ushort)0;

            _logger.LogDebug("Updated alarm breach registers for Ch{ChId}/Zone{ZId} at addr {Addr}",
                channelId, zoneId, baseAddr);
        }

        /// <summary>
        /// Updates zone alarm coils with current alarm states.
        /// Called when ZoneStateUpdatedEvent is received with updated alarm states.
        /// Uses dynamic addressing based on enabled zones only.
        /// </summary>
        /// <param name="coils">The coil array</param>
        /// <param name="channelId">The channel ID</param>
        /// <param name="zoneId">The zone ID</param>
        /// <param name="state">The zone state containing alarm flags</param>
        public void UpdateZoneAlarmCoils(bool[] coils, int channelId, int zoneId, ZoneState state)
        {
            // Find the zone's index in the ordered enabled zone list
            int zoneIndex = 0;
            bool found = false;

            foreach (var kvp in _zoneToRegisterMap.OrderBy(k => k.Key.ChannelId).ThenBy(k => k.Key.ZoneId))
            {
                if (kvp.Key.ChannelId == channelId && kvp.Key.ZoneId == zoneId)
                {
                    found = true;
                    break;
                }
                zoneIndex++;
            }

            if (!found)
            {
                _logger.LogDebug("Zone Ch{ChId}/Zone{ZId} not in enabled zone map, skipping alarm coil update", channelId, zoneId);
                return;
            }

            // Use dynamic zone alarm coil start address
            int baseCoil = _zoneAlarmCoilStart + (zoneIndex * 4);

            if (baseCoil + 3 >= coils.Length)
            {
                _logger.LogWarning("Alarm coils out of bounds for Ch{ChId}/Zone{ZId}", channelId, zoneId);
                return;
            }

            // Update the 4 alarm coils for this zone
            coils[baseCoil + 0] = state.PreAlarm;
            coils[baseCoil + 1] = state.MaxAlarm;
            coils[baseCoil + 2] = state.RorAlarm;
            coils[baseCoil + 3] = state.DeviationAlarm;

            _logger.LogDebug("Updated alarm coils for Ch{ChId}/Zone{ZId} at coil {CoilIdx}: Pre={Pre}, Max={Max}, RoR={Ror}, Dev={Dev}",
                channelId, zoneId, baseCoil,
                state.PreAlarm, state.MaxAlarm, state.RorAlarm, state.DeviationAlarm);
        }

        /// <summary>
        /// Updates channel fiber alarm coil based on whether any zone has active alarms.
        /// Uses dynamic addressing based on enabled channels only.
        /// </summary>
        /// <param name="coils">The coil array</param>
        /// <param name="channelId">The channel ID</param>
        /// <param name="hasActiveAlarms">True if any zone in the channel has active alarms</param>
        public void UpdateChannelFiberAlarmCoil(bool[] coils, int channelId, bool hasActiveAlarms)
        {
            // Look up the channel's index in enabled channels
            if (!_channelToIndexMap.TryGetValue(channelId, out int channelIndex))
            {
                _logger.LogDebug("Channel {ChId} not in enabled channel map, skipping fiber alarm coil update", channelId);
                return;
            }

            // Use dynamic fiber alarm coil start address
            int coilIndex = _fiberAlarmCoilStart + channelIndex;
            if (coilIndex < coils.Length)
            {
                coils[coilIndex] = hasActiveAlarms;
                _logger.LogDebug("Updated fiber alarm coil for Ch{ChId} at coil {CoilIdx}: {Status}",
                    channelId, coilIndex, hasActiveAlarms);
            }
        }

        /// <summary>
        /// Updates system health status coil (coil 0).
        /// Coil 0 = true (NORMAL) when no health faults, false (FAULT) when any health fault exists.
        /// </summary>
        public void UpdateSystemHealthStatusCoil(bool[] coils, bool hasHealthFaults)
        {
            if (coils.Length > 0)
            {
                // Coil 0: System Status = true (NORMAL/no faults), false (FAULT/has faults)
                coils[0] = hasHealthFaults;
                _logger.LogDebug("Updated system health status coil 0: {Status}",
                    hasHealthFaults ? "FAULT" : "NORMAL");
            }
        }

        /// <summary>
        /// Updates fiber break coil for a specific channel.
        /// Uses dynamic addressing based on enabled channels only.
        /// </summary>
        /// <param name="coils">The coil array</param>
        /// <param name="channelId">The channel ID</param>
        /// <param name="isBroken">True if fiber is broken, false if normal</param>
        public void UpdateFiberBreakCoil(bool[] coils, int channelId, bool isBroken)
        {
            // Look up the channel's index in enabled channels
            if (!_channelToIndexMap.TryGetValue(channelId, out int channelIndex))
            {
                _logger.LogDebug("Channel {ChId} not in enabled channel map, skipping fiber break coil update", channelId);
                return;
            }

            // Fiber broken coils start at coil 2
            int coilIndex = 2 + channelIndex;
            if (coilIndex < coils.Length)
            {
                coils[coilIndex] = isBroken;
                _logger.LogDebug("Updated fiber break coil for Ch{ChId} at coil {CoilIdx}: {Status}",
                    channelId, coilIndex, isBroken ? "BROKEN" : "NORMAL");
            }
        }

        /// <summary>
        /// Updates fiber break position register for a specific channel.
        /// Uses dynamic addressing based on enabled channels only.
        /// </summary>
        /// <param name="holdingRegisters">The holding register array</param>
        /// <param name="channelId">The channel ID</param>
        /// <param name="position">The fiber break position in meters, or null if no break</param>
        public void UpdateFiberBreakPosition(ushort[] holdingRegisters, int channelId, double? position)
        {
            // Look up the channel's index in enabled channels
            if (!_channelToIndexMap.TryGetValue(channelId, out int channelIndex))
            {
                _logger.LogDebug("Channel {ChId} not in enabled channel map, skipping fiber break position update", channelId);
                return;
            }

            // Use dynamic fiber break register start address
            int registerIndex = _fiberBreakRegisterStart + channelIndex;
            if (registerIndex < holdingRegisters.Length)
            {
                holdingRegisters[registerIndex] = position.HasValue
                    ? (ushort)position.Value
                    : (ushort)0;
                _logger.LogDebug("Updated fiber break position for Ch{ChId} at register {RegIdx}: {Position}m",
                    channelId, registerIndex, position?.ToString() ?? "None");
            }
        }
    }
}
