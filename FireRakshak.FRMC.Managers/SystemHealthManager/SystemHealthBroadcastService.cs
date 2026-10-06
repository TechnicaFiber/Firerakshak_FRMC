using FireRakshak.FRMC.Core.EventAggregator;
using FireRakshak.FRMC.Core.Events;
using FireRakshak.FRMC.Core.Models;
using FireRakshak.FRMC.Core.Enums;
using FireRakshak.FRMC.Host;
using FireRakshak.FRMC.Managers.SystemConfigurationManager;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FireRakshak.FRMC.Managers.SystemHealthManager
{
    /// <summary>
    /// Sends system health updates to DTSCM and broadcasts health fault events in real-time.
    /// Broadcast interval is configurable via SystemConfiguration.HealthStatusBroadcastInterval.
    /// </summary>
    public class SystemHealthBroadcastService : BackgroundService
    {
        private readonly ISystemHealthManager _systemHealthManager;
        private readonly IEventAggregator _eventAggregator;
        private readonly IConfigurationManager _configurationManager;
        private readonly WebSocketServer _wsServer;
        private readonly ILogger<SystemHealthBroadcastService> _logger;

        private TimeSpan _interval;

        public SystemHealthBroadcastService(
            ISystemHealthManager systemHealthManager,
            IEventAggregator eventAggregator,
            IConfigurationManager configurationManager,
            WebSocketServer wsServer,
            ILogger<SystemHealthBroadcastService> logger)
        {
            _systemHealthManager = systemHealthManager;
            _eventAggregator = eventAggregator;
            _configurationManager = configurationManager;
            _wsServer = wsServer;
            _logger = logger;

            // Default interval (will be updated in StartAsync)
            _interval = TimeSpan.FromMinutes(5);

            // Subscribe to health fault events for real-time broadcasting
            _eventAggregator.Subscribe<SystemHealthFaultEvent>(HandleSystemHealthFaultEvent);

            // Subscribe to configuration changes
            _eventAggregator.Subscribe<ConfigurationChangedEvent>(HandleConfigurationChanged);
        }

        private Task HandleConfigurationChanged(ConfigurationChangedEvent ev)
        {
            if (ev.ConfigType == ConfigurationType.System && ev.Changes.ContainsKey("HealthStatusBroadcastInterval"))
            {
                var newInterval = (int)ev.Changes["HealthStatusBroadcastInterval"];
                _interval = TimeSpan.FromMilliseconds(newInterval);
                _logger.LogInformation("Health status broadcast interval updated to {Interval}ms", newInterval);
            }
            return Task.CompletedTask;
        }

        public override async Task StartAsync(CancellationToken stoppingToken)
        {
            // Initialize interval from system configuration
            var sysConfig = await _configurationManager.GetSystemConfigurationAsync();
            _interval = TimeSpan.FromMilliseconds(sysConfig.HealthStatusBroadcastInterval);
            _logger.LogInformation("SystemHealthBroadcastService starting with broadcast interval {Interval}ms", sysConfig.HealthStatusBroadcastInterval);

            await base.StartAsync(stoppingToken);
        }


        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("SystemHealthBroadcastService started.");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    JsonElement payload = _systemHealthManager.CurrentHealthPayload;

                    // ✅ IMPORTANT: Never skip broadcast
                    if (payload.ValueKind == JsonValueKind.Undefined)
                    {
                        _logger.LogWarning(
                            "System health payload empty — sending fallback health (DTS NOT responsive)");

                        payload = JsonSerializer.SerializeToElement(new
                        {
                            LampStatus = false,
                            DtsResponsive = false,   // 🔴 THIS makes DTSCM LED turn RED
                            ModuleTemperature = 0,
                            ApdValue = 0,
                            LaserCurrent = 0,
                            PumpCurrent = 0,
                            LastCheck = DateTime.UtcNow
                        });
                    }

                    var message = new WebSocketMessage("SystemHealthUpdate", payload);
                    await _wsServer.BroadcastMessageAsync(message);

                    //_logger.LogInformation("System Health payload sent to DTSCM.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to broadcast system health update.");
                }

                await Task.Delay(_interval, stoppingToken);
            }

            _logger.LogInformation("SystemHealthBroadcastService stopping.");
        }

        /// <summary>
        /// Handles SystemHealthFaultEvent by broadcasting health fault information to WebSocket clients.
        /// </summary>
        private async Task HandleSystemHealthFaultEvent(SystemHealthFaultEvent ev)
        {
            try
            {
                var payload = new
                {
                    FaultId = ev.Fault.FaultId,
                    FaultType = ev.Fault.FaultType.ToString(),
                    IsActive = ev.Fault.IsActive,
                    IsNewFault = ev.IsNewFault,
                    CurrentValue = ev.Fault.CurrentValue,
                    ThresholdValue = ev.Fault.ThresholdValue,
                    Description = ev.Fault.Description,
                    Details = ev.Fault.Details,
                    FirstDetectedAt = ev.Fault.FirstDetectedAt,
                    LastUpdatedAt = ev.Fault.LastUpdatedAt,
                    ClearedAt = ev.Fault.ClearedAt,
                    Timestamp = ev.Timestamp
                };

                var payloadElement = JsonSerializer.SerializeToElement(payload);

                // Use IsActive to determine message type (not IsNewFault)
                // IsActive=true means fault is active (new or updated)
                // IsActive=false means fault was cleared
                var message = new WebSocketMessage(
                    ev.Fault.IsActive ? "health_fault_detected" : "health_fault_cleared",
                    payloadElement);

                await _wsServer.BroadcastMessageAsync(message);

                _logger.LogInformation(
                    "Health fault WebSocket message sent: Type={FaultType}, IsNew={IsNew}, Active={IsActive}, MessageType={MessageType}",
                    ev.Fault.FaultType, ev.IsNewFault, ev.Fault.IsActive,
                    ev.Fault.IsActive ? "health_fault_detected" : "health_fault_cleared");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to broadcast health fault event");
            }
        }
    }
}
