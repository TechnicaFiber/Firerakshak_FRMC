using FireRakshak.FRMC.Core.Enums;
using FireRakshak.FRMC.Core.EventAggregator;
using FireRakshak.FRMC.Core.Events;
using FireRakshak.FRMC.Core.Models;
using FireRakshak.FRMC.Managers.AlarmManager;
using FireRakshak.FRMC.Managers.ChannelsManager;
using FireRakshak.FRMC.Managers.SystemHealthManager;
using Microsoft.Extensions.Logging;
using NModbus;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace FireRakshak.FRMC.Managers.ExternalCommunicationManager
{
    /// <summary>
    /// Implements a Modbus TCP server exposing system registers with dynamic zone support.
    /// Provides thread-safe access to register data and real-time updates from zone state changes.
    /// </summary>
    public class ModbusTcpServer : IAsyncDisposable, IDisposable
    {
        private readonly ModbusRegisterMapper _registerMapper;
        private readonly IEventAggregator _eventAggregator;
        private readonly IChannelsManager _channelsManager;
        private readonly IAlarmManager _alarmManager;
        private readonly Dictionary<(int channelId, int zoneId), HashSet<int>> _lastAlarmIds = new();
        private readonly ISystemHealthManager _systemHealthManager;
        private readonly ILogger<ModbusTcpServer> _logger;

        private TcpListener? _listener;
        private IModbusSlaveNetwork? _network;
        private IModbusSlave? _slave;
        private CancellationTokenSource? _cts;

        private bool[] _coils; // Dynamically sized based on zone count
        private ushort[] _holdingRegisters; // Dynamically sized based on zone count
        private readonly ReaderWriterLockSlim _registerLock = new ReaderWriterLockSlim();

        // Debounce configuration changes to avoid multiple rapid reinitializations
        private Timer? _configChangeDebounceTimer;
        private readonly object _debounceTimerLock = new object();
        private const int CONFIG_CHANGE_DEBOUNCE_MS = 500; // Wait 500ms after last change before reinitializing

        public ModbusTcpServer(
            ModbusRegisterMapper registerMapper,
            ILogger<ModbusTcpServer> logger,
            IEventAggregator eventAggregator,
            IChannelsManager channelsManager,
            IAlarmManager alarmManager,
            ISystemHealthManager systemHealthManager)
        {
            _registerMapper = registerMapper;
            _logger = logger;
            _eventAggregator = eventAggregator;
            _channelsManager = channelsManager;
            _alarmManager = alarmManager;
            _systemHealthManager = systemHealthManager;

            // Subscribe to zone state updates for real-time register updates
            _eventAggregator.Subscribe<ZoneStateUpdatedEvent>(HandleZoneStateUpdatedEvent);

            // Subscribe to channel event time changes for alarm timestamp registers
            _eventAggregator.Subscribe<ChannelEventTimeChangedEvent>(HandleChannelEventTimeChangedEvent);

            // Subscribe to configuration changes to reinitialize Modbus server
            _eventAggregator.Subscribe<ConfigurationChangedEvent>(HandleConfigurationChangedEvent);

            // Subscribe to system health fault events for health monitoring registers/coils
            _eventAggregator.Subscribe<SystemHealthFaultEvent>(HandleSystemHealthFaultEvent);

            // Subscribe to fiber break events for fiber break coils and position registers
            _eventAggregator.Subscribe<FiberBreakDetectedEvent>(HandleFiberBreakDetectedEvent);
        }

        /// <summary>
        /// Handles zone state updates and synchronizes them to Modbus registers and coils.
        /// Updates real-time temperature values, alarm breach registers, and alarm coils.
        /// </summary>
        private async Task HandleZoneStateUpdatedEvent(ZoneStateUpdatedEvent ev)
        {
            if (_holdingRegisters == null || _coils == null)
               return;

            var alarms = await _alarmManager.GetActiveAlarmsAsync() ?? new List<ActiveAlarm>();
            _registerLock.EnterWriteLock();
            try
            {
                // Update real-time temperature values in holding registers
                _registerMapper.UpdateZoneRegisters(
                    _holdingRegisters,
                    ev.ChannelId,
                    ev.ZoneId,
                    ev.State);

                // Update alarm breach values and positions in holding registers

                var zoneAlarmIds = alarms
                    .Where(a => a.ChannelId == ev.ChannelId && a.ZoneId == ev.ZoneId)
                    .Select(a => a.AlarmId)
                    .ToHashSet();

                var key = (ev.ChannelId, ev.ZoneId);

                // get previous
                _lastAlarmIds.TryGetValue(key, out var previousIds);

                // check change
                bool hasChanged =
                    previousIds == null ||
                    !previousIds.SetEquals(zoneAlarmIds);

                // 🔥 ONLY update when changed
                if (hasChanged)
                {
                    _registerMapper.UpdateZoneAlarmRegisters(
                        _holdingRegisters,
                        ev.ChannelId,
                        ev.ZoneId,
                        ev.State);

                    // update cache
                    _lastAlarmIds[key] = zoneAlarmIds;

                    // Update zone alarm coils (PreAlarm, MaxAlarm, RorAlarm, DeviationAlarm)
                    _registerMapper.UpdateZoneAlarmCoils(
                        _coils,
                        ev.ChannelId,
                        ev.ZoneId,
                        ev.State);
                }                

                // Update channel alarm coil (coil 10-17) by checking ALL zones in channel
                var channels = _channelsManager.GetChannels();
                var channel = channels.FirstOrDefault(c => c.Configuration.ChannelId == ev.ChannelId);
                bool hasActiveAlarms = alarms.Any(a => a.ChannelId == ev.ChannelId && (a.Type == AlarmType.PreAlarm || a.Type == AlarmType.MaxTemperature || a.Type == AlarmType.RateOfRise || a.Type == AlarmType.ZoneDeviation));
                _registerMapper.UpdateChannelFiberAlarmCoil(
                    _coils,
                    ev.ChannelId,
                    hasActiveAlarms);
            }
            finally
            {
                _registerLock.ExitWriteLock();
            }

            return ;
        }

        /// <summary>
        /// Handles channel event time changes and updates channel event time registers.
        /// </summary>
        private Task HandleChannelEventTimeChangedEvent(ChannelEventTimeChangedEvent ev)
        {
            if (_holdingRegisters == null)
                return Task.CompletedTask;

            _registerLock.EnterWriteLock();
            try
            {
                _registerMapper.UpdateChannelEventRegisters(
                    _holdingRegisters,
                    ev.ChannelId,
                    ev.LastAlarmTime);
            }
            finally
            {
                _registerLock.ExitWriteLock();
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Handles configuration changes (Channel/Zone) with debouncing to avoid multiple rapid reinitializations.
        /// Waits for CONFIG_CHANGE_DEBOUNCE_MS after the last change before reinitializing.
        /// This dramatically improves performance when adding/deleting many zones at once.
        /// </summary>
        private Task HandleConfigurationChangedEvent(ConfigurationChangedEvent ev)
        {
            // Only handle Channel and Zone configuration changes
            if (ev.ConfigType != ConfigurationType.Channel &&
                ev.ConfigType != ConfigurationType.Zone)
            {
                return Task.CompletedTask;
            }

            _logger.LogDebug("Configuration changed ({Type}), scheduling debounced Modbus reinitialization...", ev.ConfigType);

            // Use debouncing to batch multiple rapid configuration changes
            lock (_debounceTimerLock)
            {
                // Cancel any existing pending reinitialization
                _configChangeDebounceTimer?.Dispose();

                // Schedule a new reinitialization after the debounce period
                _configChangeDebounceTimer = new Timer(
                    async _ => await ExecuteDebouncedReinitializationAsync(),
                    null,
                    CONFIG_CHANGE_DEBOUNCE_MS,
                    Timeout.Infinite // Don't repeat
                );
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Executes the actual Modbus reinitialization after debounce period has elapsed.
        /// </summary>
        private async Task ExecuteDebouncedReinitializationAsync()
        {
            _logger.LogInformation("Debounce period elapsed, reinitializing Modbus server...");

            try
            {
                await ReinitializeModbusDataAsync();
                _logger.LogInformation("Modbus server reinitialized successfully after configuration changes");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reinitialize Modbus server after configuration change");
            }
        }

        /// <summary>
        /// Handles system health fault events and updates coil 0 (system health status).
        /// </summary>
        private async Task HandleSystemHealthFaultEvent(SystemHealthFaultEvent ev)
        {
            if (_coils == null)
                return;

            _registerLock.EnterWriteLock();
            try
            {
                // Get all active faults to determine overall system health status
                var allActiveFaults = await _systemHealthManager.GetActiveHealthFaultsAsync();
                bool hasHealthFaults = allActiveFaults.Any(f => f.IsActive);

                // Update coil 0 (System Status) based on health faults
                _registerMapper.UpdateSystemHealthStatusCoil(_coils, hasHealthFaults);

                _logger.LogDebug("Updated Modbus system health status coil 0 for fault: {FaultType}, OverallStatus={Status}",
                    ev.Fault.FaultType, hasHealthFaults ? "FAULT" : "NORMAL");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating Modbus system health status for fault {FaultType}",
                    ev.Fault.FaultType);
            }
            finally
            {
                _registerLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Handles fiber break detected events and updates Modbus coils and registers.
        /// Updates fiber break coil (coils 2-9) and fiber break position register (registers 5-12).
        /// </summary>
        private Task HandleFiberBreakDetectedEvent(FiberBreakDetectedEvent ev)
        {
            if (_coils == null || _holdingRegisters == null)
                return Task.CompletedTask;

            _registerLock.EnterWriteLock();
            try
            {
                // Update fiber break coil (coils 2-9)
                _registerMapper.UpdateFiberBreakCoil(_coils, ev.ChannelId, ev.IsBroken);

                // Update fiber break position register (registers 5-12)
                _registerMapper.UpdateFiberBreakPosition(_holdingRegisters, ev.ChannelId,
                    ev.IsBroken ? ev.Position : null);

                _logger.LogDebug("Updated Modbus fiber break for Ch{ChId}: Broken={IsBroken}, Position={Position}m",
                    ev.ChannelId, ev.IsBroken, ev.Position);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating Modbus fiber break status for channel {ChId}",
                    ev.ChannelId);
            }
            finally
            {
                _registerLock.ExitWriteLock();
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Reinitializes Modbus coils and registers when channel/zone configuration changes.
        /// Resizes arrays dynamically, recreates data store, and restarts the Modbus network.
        /// </summary>
        private async Task ReinitializeModbusDataAsync()
        {
            // Only reinitialize if server is running
            if (_listener == null || _network == null || _slave == null)
            {
                _logger.LogDebug("Modbus server not started yet, skipping reinitialization");
                return;
            }

            _logger.LogInformation("Configuration changed, restarting Modbus server with new array sizes...");

            // Get latest channel configuration
            var channels = _channelsManager.GetChannels();

            // Calculate new array sizes based on enabled channels/zones only
            int newCoilSize = CalculateCoilArraySize(channels);
            int newRegisterSize = CalculateRegisterArraySize(channels);

            var enabledChannels = channels.Where(c => c.Configuration.IsEnabled).ToList();
            var enabledZones = enabledChannels.SelectMany(c => c.Zones.Where(z => z.Configuration.Enabled)).Count();
            _logger.LogInformation("Reconfiguring for {Channels} enabled channels with {Zones} enabled zones",
                enabledChannels.Count, enabledZones);

            // Acquire write lock to safely replace arrays and restart network
            _registerLock.EnterWriteLock();
            try
            {
                // Step 1: Stop the current network (closes active connections)
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;

                // Dispose old network and slave
                (_network as IDisposable)?.Dispose();
                _network = null;
                _slave = null;

                _logger.LogInformation("Stopped Modbus network, reallocating arrays...");

                // Step 2: Reallocate arrays with new sizes
                _coils = new bool[newCoilSize];
                _holdingRegisters = new ushort[newRegisterSize];

                // Step 3: Initialize arrays from current channel and zone states
                _registerMapper.InitializeCoils(_coils, channels);
                await _registerMapper.InitializeRegisters(_holdingRegisters, channels);

                // Initialize system health status coil (coil 0)
                var activeFaults = await _systemHealthManager.GetActiveHealthFaultsAsync();
                bool hasHealthFaults = activeFaults.Any(f => f.IsActive);
                _registerMapper.UpdateSystemHealthStatusCoil(_coils, hasHealthFaults);

                _logger.LogInformation("Arrays reinitialized: {Coils} coils, {Registers} registers",
                    newCoilSize, newRegisterSize);

                // Step 4: Create new data store with new arrays
                var factory = new ModbusFactory();
                var dataStore = new ThreadSafeDataStore(_coils, _holdingRegisters, _registerLock, _eventAggregator, _logger);

                // Step 5: Recreate slave and network with new data store
                _slave = factory.CreateSlave(1, dataStore);
                _network = factory.CreateSlaveNetwork(_listener);
                _network.AddSlave(_slave);

                // Step 6: Restart listening
                _cts = new CancellationTokenSource();
                Task.Run(async () =>
                {
                    try
                    {
                        _logger.LogInformation("Modbus TCP server restarted, listening for connections...");
                        await _network.ListenAsync(_cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogInformation("Modbus server stopped listening.");
                    }
                    catch (System.Net.Sockets.SocketException ex) when (ex.ErrorCode == 995)
                    {
                        // Expected during reinitialization: AcceptTcpClientAsync aborts when listener is cancelled
                        _logger.LogInformation("Modbus server listener aborted during reinitialization.");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Modbus network error");
                    }
                }, _cts.Token);

                _logger.LogInformation("Modbus server successfully reinitialized with new configuration");
            }
            finally
            {
                _registerLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Calculates the required register array size based on enabled channels and zones.
        /// </summary>
        private int CalculateRegisterArraySize(List<Channel> channels)
        {
            var enabledChannels = channels.Where(c => c.Configuration.IsEnabled).ToList();
            int enabledZones = enabledChannels.SelectMany(c => c.Zones.Where(z => z.Configuration.Enabled)).Count();
            int enabledChannelCount = enabledChannels.Count;

            // Base registers: 0-4 (5 registers: Reset, TimeValid, Time1-3)
            int baseRegisters = 5;

            // Fiber break positions: M enabled channels
            int fiberBreakRegisters = enabledChannelCount;

            // Zone data: 5 registers per enabled zone
            int zoneDataRegisters = enabledZones * 5;

            // Channel events: 3 registers per enabled channel
            int channelEventRegisters = enabledChannelCount * 3;

            // Zone thresholds: 8 registers per enabled zone
            int thresholdRegisters = enabledZones * 8;

            int totalSize = baseRegisters + fiberBreakRegisters + zoneDataRegisters +
                           channelEventRegisters + thresholdRegisters;

            _logger.LogInformation("Calculated register array size: {Size} ({Channels} enabled channels, {Zones} enabled zones)",
                totalSize, enabledChannelCount, enabledZones);

            return totalSize;
        }

        /// <summary>
        /// Calculates the required coil array size based on enabled channels and zones.
        /// </summary>
        private int CalculateCoilArraySize(List<Channel> channels)
        {
            var enabledChannels = channels.Where(c => c.Configuration.IsEnabled).ToList();
            int enabledZones = enabledChannels.SelectMany(c => c.Zones.Where(z => z.Configuration.Enabled)).Count();
            int enabledChannelCount = enabledChannels.Count;

            // Coil 0-1: System Status, Running Status (2 coils)
            // Coils 2 to (2+M-1): Fiber Broken flags (M enabled channels)
            // Coils (2+M) to (2+2M-1): Fiber Alarm flags (M enabled channels)
            // Coils (2+2M)+: Zone alarms (4 per enabled zone)

            int baseCoils = 2; // System Status + Running Status
            int fiberCoils = enabledChannelCount * 2; // Broken + Alarm per channel
            int zoneAlarmCoils = enabledZones * 4; // PreAlarm, MaxAlarm, RorAlarm, DeviationAlarm

            int totalSize = baseCoils + fiberCoils + zoneAlarmCoils;

            _logger.LogInformation("Calculated coil array size: {Size} ({Channels} enabled channels, {Zones} enabled zones)",
                totalSize, enabledChannelCount, enabledZones);

            return totalSize;
        }

        /// <summary>
        /// Starts the Modbus TCP server with dynamic register allocation.
        /// </summary>
        public async Task StartAsync(string ip = "0.0.0.0", int port = 502)
        {
            _logger.LogInformation("Starting Modbus TCP Server on {Ip}:{Port}", ip, port);

            // Get all channels and zones from configuration
            var channels = _channelsManager.GetChannels();
            var enabledChannels = channels.Where(c => c.Configuration.IsEnabled).ToList();
            int enabledZones = enabledChannels.SelectMany(c => c.Zones.Where(z => z.Configuration.Enabled)).Count();

            _logger.LogInformation("Detected {Channels} enabled channels with {Zones} enabled zones",
                enabledChannels.Count, enabledZones);

            // Allocate coils and registers dynamically based on enabled channel/zone count
            int coilSize = CalculateCoilArraySize(channels);
            _coils = new bool[coilSize];

            int registerSize = CalculateRegisterArraySize(channels);
            _holdingRegisters = new ushort[registerSize];

            // Initialize coils and registers with actual channel and zone states
            _registerLock.EnterWriteLock();
            try
            {
                _registerMapper.InitializeCoils(_coils, channels);
                await _registerMapper.InitializeRegisters(_holdingRegisters, channels);

                // Initialize system health status coil (coil 0)
                var activeFaults = await _systemHealthManager.GetActiveHealthFaultsAsync();
                bool hasHealthFaults = activeFaults.Any(f => f.IsActive);
                _registerMapper.UpdateSystemHealthStatusCoil(_coils, hasHealthFaults);
            }
            finally
            {
                _registerLock.ExitWriteLock();
            }

            // Create TCP listener
            _listener = new TcpListener(IPAddress.Parse(ip), port);
            _listener.Start();

            // Create Modbus slave with thread-safe data store
            var factory = new ModbusFactory();
            var dataStore = new ThreadSafeDataStore(_coils, _holdingRegisters, _registerLock, _eventAggregator, _logger);

            _slave = factory.CreateSlave(1, dataStore);
            _network = factory.CreateSlaveNetwork(_listener);
            _network.AddSlave(_slave);

            // Start listening for Modbus TCP connections
            _cts = new CancellationTokenSource();

            Task.Run(async () =>
            {
                try
                {
                    _logger.LogInformation("Modbus TCP server listening for connections...");
                    await _network.ListenAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Modbus server stopped listening.");
                }
                catch (System.Net.Sockets.SocketException ex) when (ex.ErrorCode == 995)
                {
                    _logger.LogInformation("Modbus server listener aborted during shutdown.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Modbus network error");
                }
            }, _cts.Token);

            _logger.LogInformation("Modbus TCP Server started successfully");
        }

        /// <summary>
        /// Stops the Modbus TCP server and releases resources.
        /// </summary>
        public Task StopAsync()
        {
            _logger.LogInformation("Stopping Modbus TCP Server...");
            try
            {
                _cts?.Cancel();
                _listener?.Stop();
                (_network as IDisposable)?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error while stopping server");
            }

            _listener = null;
            _network = null;
            _slave = null;
            _cts = null;

            _logger.LogInformation("Modbus TCP Server stopped.");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Asynchronously disposes the Modbus server and associated resources.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            // Dispose debounce timer first to prevent any pending reinitializations
            lock (_debounceTimerLock)
            {
                _configChangeDebounceTimer?.Dispose();
                _configChangeDebounceTimer = null;
            }

            await StopAsync();
            _registerLock?.Dispose();
        }

        /// <summary>
        /// Synchronously disposes the Modbus server - avoid if possible, use DisposeAsync instead.
        /// </summary>
        public void Dispose()
        {
            // Dispose debounce timer first to prevent any pending reinitializations
            lock (_debounceTimerLock)
            {
                _configChangeDebounceTimer?.Dispose();
                _configChangeDebounceTimer = null;
            }

            // Use synchronous disposal only as fallback - prefer DisposeAsync
            try
            {
                StopAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during synchronous disposal of Modbus server");
            }
            finally
            {
                _registerLock?.Dispose();
            }
        }

        /// <summary>
        /// Thread-safe implementation of ISlaveDataStore.
        /// Wraps register arrays with read/write locking for concurrent access safety.
        /// </summary>
        private class ThreadSafeDataStore : ISlaveDataStore
        {
            public IPointSource<bool> CoilDiscretes { get; }
            public IPointSource<bool> CoilInputs { get; }
            public IPointSource<ushort> HoldingRegisters { get; }
            public IPointSource<ushort> InputRegisters { get; }

            public ThreadSafeDataStore(bool[] coils, ushort[] holdingRegisters, ReaderWriterLockSlim lockObj,
                IEventAggregator eventAggregator, ILogger<ModbusTcpServer> logger)
            {
                CoilDiscretes = new ThreadSafeArrayPointSource<bool>(coils, lockObj);
                CoilInputs = new ThreadSafeArrayPointSource<bool>(coils, lockObj);
                HoldingRegisters = new ThreadSafeArrayPointSourceWithCallback<ushort>(holdingRegisters, lockObj, eventAggregator, logger);
                InputRegisters = new ThreadSafeArrayPointSource<ushort>(holdingRegisters, lockObj);
            }
        }

        /// <summary>
        /// Thread-safe point source implementation.
        /// Uses ReaderWriterLockSlim for efficient concurrent reads with exclusive writes.
        /// </summary>
        private class ThreadSafeArrayPointSource<T> : IPointSource<T>
        {
            private readonly T[] _data;
            private readonly ReaderWriterLockSlim _lock;

            public ThreadSafeArrayPointSource(T[] data, ReaderWriterLockSlim lockObj)
            {
                _data = data;
                _lock = lockObj;
            }

            public T[] ReadPoints(ushort start, ushort count)
            {
                _lock.EnterReadLock();
                try
                {
                    var result = new T[count];
                    for (int i = 0; i < count; i++)
                    {
                        var idx = start + i;
                        result[i] = idx < _data.Length ? _data[idx] : default!;
                    }
                    return result;
                }
                finally
                {
                    _lock.ExitReadLock();
                }
            }

            public void WritePoint(ushort index, T value)
            {
                _lock.EnterWriteLock();
                try
                {
                    if (index < _data.Length)
                        _data[index] = value;
                }
                finally
                {
                    _lock.ExitWriteLock();
                }
            }

            public void WritePoints(ushort start, T[] values)
            {
                _lock.EnterWriteLock();
                try
                {
                    for (int i = 0; i < values.Length; i++)
                    {
                        var idx = start + i;
                        if (idx < _data.Length)
                            _data[idx] = values[i];
                    }
                }
                finally
                {
                    _lock.ExitWriteLock();
                }
            }
        }

        /// <summary>
        /// Thread-safe point source with callback support for monitoring specific register writes.
        /// Monitors register 0 (Reset Control) and publishes AlarmResetRequestedEvent when written.
        /// </summary>
        private class ThreadSafeArrayPointSourceWithCallback<T> : IPointSource<T>
        {
            private readonly T[] _data;
            private readonly ReaderWriterLockSlim _lock;
            private readonly IEventAggregator _eventAggregator;
            private readonly ILogger<ModbusTcpServer> _logger;

            public ThreadSafeArrayPointSourceWithCallback(T[] data, ReaderWriterLockSlim lockObj,
                IEventAggregator eventAggregator, ILogger<ModbusTcpServer> logger)
            {
                _data = data;
                _lock = lockObj;
                _eventAggregator = eventAggregator;
                _logger = logger;
            }

            public T[] ReadPoints(ushort start, ushort count)
            {
                _lock.EnterReadLock();
                try
                {
                    var result = new T[count];
                    for (int i = 0; i < count; i++)
                    {
                        var idx = start + i;
                        result[i] = idx < _data.Length ? _data[idx] : default!;
                    }
                    return result;
                }
                finally
                {
                    _lock.ExitReadLock();
                }
            }

            public void WritePoint(ushort index, T value)
            {
                _lock.EnterWriteLock();
                try
                {
                    if (index < _data.Length)
                        _data[index] = value;

                    // Monitor register 0 (Reset Control) - value 1=RESET, 2=MUTE (per spec)
                    if (index == 0 && value is ushort ushortValue && ushortValue == 1)
                    {
                        // RESET (value=1) - clear alarms
                        // Note: MUTE (value=2) not implemented yet - ignored for now
                        _logger.LogWarning("Modbus Reset Control: RESET (value=1) - Triggering alarm reset");

                        // Publish alarm reset event asynchronously with proper error handling
                        _ = PublishAlarmResetEventSafeAsync();

                        // Reset register back to 0 (idle state)
                        _data[0] = (T)(object)(ushort)0;
                    }
                }
                finally
                {
                    _lock.ExitWriteLock();
                }
            }

            public void WritePoints(ushort start, T[] values)
            {
                _lock.EnterWriteLock();
                try
                {
                    for (int i = 0; i < values.Length; i++)
                    {
                        var idx = start + i;
                        if (idx < _data.Length)
                        {
                            _data[idx] = values[i];

                            // Monitor register 0 in multi-write - value 1=RESET, 2=MUTE (per spec)
                            if (idx == 0 && values[i] is ushort ushortValue && ushortValue == 1)
                            {
                                // RESET (value=1) - clear alarms
                                // Note: MUTE (value=2) not implemented yet - ignored for now
                                _logger.LogWarning("Modbus Reset Control: RESET (value=1) - Triggering alarm reset");

                                // Publish alarm reset event asynchronously with proper error handling
                                _ = PublishAlarmResetEventSafeAsync();

                                // Reset register back to 0 (idle state)
                                _data[0] = (T)(object)(ushort)0;
                            }
                        }
                    }
                }
                finally
                {
                    _lock.ExitWriteLock();
                }
            }

            /// <summary>
            /// Safely publishes alarm reset event with proper error handling.
            /// Runs asynchronously without blocking the Modbus write operation.
            /// </summary>
            private async Task PublishAlarmResetEventSafeAsync()
            {
                try
                {
                    await _eventAggregator.PublishAsync(
                        new AlarmResetRequestedEvent("Modbus", DateTime.UtcNow)
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to publish alarm reset event from Modbus write");
                }
            }
        }
    }
}
