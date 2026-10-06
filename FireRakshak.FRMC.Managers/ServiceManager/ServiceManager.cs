using System;
using System.Threading.Tasks;
using FireRakshak.FRMC.Core.Enums;
using FireRakshak.FRMC.Core.EventAggregator;
using FireRakshak.FRMC.Core.Events;
using FireRakshak.FRMC.Managers.ExternalInterfacesManager;
using FireRakshak.FRMC.Infrastructure.Security;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Text.Json;
using FireRakshak.FRMC.Core.Models;

namespace FireRakshak.FRMC.Managers.ServiceManager
{
    public class ServiceManager : IServiceManager
    {
        private readonly IExternalInterfacesManager _externalInterfacesManager;
        private readonly UserStore _userStore;
        private readonly ILogger<ServiceManager> _logger;
        private readonly IEventAggregator _eventAggregator;

        public ServiceManager(
            IExternalInterfacesManager externalInterfacesManager,
            UserStore userStore,
            ILogger<ServiceManager> logger,
            IEventAggregator eventAggregator)
        {
            _externalInterfacesManager = externalInterfacesManager;
            _userStore = userStore;
            _logger = logger;
            _eventAggregator = eventAggregator;
        }

        public async Task<bool> AuthenticateUserAsync(string username, string password)
        {
            _logger.LogInformation("Attempting to authenticate user: {Username}", username);
            return await _userStore.ValidateCredentialsAsync(username, password);
        }

        public Task<UserSession> CreateSessionAsync(string username, UserRole role)
        {
            return Task.FromResult(new UserSession(Guid.NewGuid().ToString(), username, role, DateTime.UtcNow));
        }

        public async Task<object> ProcessCommandAsync(UserSession session, string command, JsonElement payload)
        {
            _logger.LogInformation("Processing command '{Command}' for user '{Username}' (Session: {SessionId})",
                command, session.Username, session.SessionId);

            switch (command.ToLowerInvariant())
            {
                // ================= EXISTING COMMANDS =======================
                case "get_external_parameters":
                    return await HandleGetExternalParametersCommand(session);

                case "alarm_reset":
                    if (payload.ValueKind == JsonValueKind.Number)
                        return await HandleAlarmResetCommand(session, payload.GetInt32());
                    return new { Success = false, Message = "Invalid payload for alarm_reset." };

                case "get_ip_config":
                    return await HandleGetIpConfigCommand(session);

                case "update_ip_config":
                    return await HandleUpdateIpConfigCommand(session, payload);

                case "get_clock_config":
                    return await HandleGetClockConfigCommand(session);

                case "update_clock_config":
                    if (payload.ValueKind == JsonValueKind.String &&
                        DateTime.TryParse(payload.GetString(), out DateTime newTime))
                    {
                        return await HandleUpdateClockConfigCommand(session, newTime);
                    }
                    return new { Success = false, Message = "Invalid datetime format." };

                case "process_network_time_sync":
                    if (payload.ValueKind == JsonValueKind.True ||
                        payload.ValueKind == JsonValueKind.False)
                    {
                        return await HandleProcessNetworkTimeSyncCommand(session, payload.GetBoolean());
                    }
                    return new { Success = false, Message = "Expected boolean." };

                // ==================== NEW CHANNEL COMMANDS ===================

                case "get_channel_config":
                    return await HandleGetChannelConfigCommand(session, payload);

                case "set_channel_config":
                    return await HandleSetChannelConfigCommand(session, payload);

                case "get_all_channels":
                    return await HandleGetAllChannelsCommand(session);

                // ==================== LASER CONTROL COMMANDS ===================

                case "set_pump_current":
                    return await HandleSetPumpCurrentCommand(session, payload);

                case "set_pulse_width":
                    return await HandleSetPulseWidthCommand(session, payload);

                default:
                    return HandleUnknownCommand(session, command);
            }
        }



        private async Task<object> HandleGetChannelConfigCommand(UserSession session, JsonElement payload)
        {
            if (session.Role != UserRole.Admin)
                return new { Success = false, Message = "Permission denied." };

            if (!payload.TryGetProperty("ChannelId", out var channelIdElement) ||
                channelIdElement.ValueKind != JsonValueKind.Number)
            {
                return new { Success = false, Message = "Invalid payload: ChannelId missing." };
            }

            int channelId = channelIdElement.GetInt32();

            var config = await _externalInterfacesManager.GetChannelConfigurationAsync(channelId);
            if (config == null)
                return new { Success = false, Message = "Channel not found." };

            return new { Success = true, Configuration = config };
        }

        private async Task<object> HandleSetChannelConfigCommand(UserSession session, JsonElement payload)
        {
            if (session.Role != UserRole.Admin)
                return new { Success = false, Message = "Permission denied." };

            ChannelConfiguration? config;

            try
            {
                config = payload.Deserialize<ChannelConfiguration>();
            }
            catch
            {
                return new { Success = false, Message = "Invalid JSON for channel configuration." };
            }

            if (config == null)
                return new { Success = false, Message = "Channel config is null." };

            // 🔥 FIXED: Use correct method
            await _externalInterfacesManager.AddOrUpdateChannelConfigurationAsync(config, session.Username);

            _logger.LogInformation("Channel {Id} config updated by {User}", config.ChannelId, session.Username);

            // Publish admin activity event for audit logging
            await _eventAggregator.PublishAsync(
                new AdminActivityEvent(
                    "UpdateChannelConfiguration",
                    session.Username,
                    "WebSocket",
                    new Dictionary<string, object>
                    {
                        { "ChannelId", config.ChannelId },
                        { "ChannelName", config.Name ?? "null" }
                    },
                    success: true));

            return new { Success = true, Message = "Channel configuration updated." };
        }

        private async Task<object> HandleGetAllChannelsCommand(UserSession session)
        {
            if (session.Role != UserRole.Admin)
                return new { Success = false, Message = "Permission denied." };

            var channels = await _externalInterfacesManager.GetAllChannelConfigurationsAsync();

            return new { Success = true, Channels = channels };
        }

        // ========================================================================
        // EXISTING HANDLERS
        // ========================================================================

        private async Task<object> HandleProcessNetworkTimeSyncCommand(UserSession session, bool disable)
        {
            if (session.Role != UserRole.Admin)
                return new { Success = false, Message = "Permission denied." };

            var (success, message) = await _externalInterfacesManager.ProcessNetworkTimeSynchronizationAsync(disable);

            return new { Success = success, Message = message };
        }

        private async Task<object> HandleGetExternalParametersCommand(UserSession session)
        {
            if (session.Role != UserRole.Admin)
                return new { Success = false, Message = "Permission denied." };

            return await _externalInterfacesManager.GetExternalParametersAsync();
        }

        private async Task<object> HandleAlarmResetCommand(UserSession session, int alarmId)
        {
            await _externalInterfacesManager.ProcessAlarmResetAsync(alarmId);
            return new { Success = true, Message = $"Alarm {alarmId} reset." };
        }

        private async Task<object> HandleGetIpConfigCommand(UserSession session)
        {
            if (session.Role != UserRole.Admin)
                return new { Success = false, Message = "Permission denied." };

            return await _externalInterfacesManager.GetIpConfigurationAsync();
        }

        private async Task<object> HandleUpdateIpConfigCommand(UserSession session, JsonElement payload)
        {
            if (session.Role != UserRole.Admin)
                return new { Success = false, Message = "Permission denied." };

            var data = payload.Deserialize<SystemIpConfigurationPayload>();
            if (data == null)
                return new { Success = false, Message = "Invalid IP payload." };

            await _externalInterfacesManager.ProcessIpConfigurationAsync(
                data.NewIpAddress, data.NewSubnetMask, data.NewGateway, data.AdapterName, session.Username);

            // Publish admin activity event for audit logging
            await _eventAggregator.PublishAsync(
                new AdminActivityEvent(
                    "UpdateIPConfiguration",
                    session.Username,
                    "WebSocket",
                    new Dictionary<string, object>
                    {
                        { "NewIpAddress", data.NewIpAddress ?? "null" },
                        { "NewSubnetMask", data.NewSubnetMask ?? "null" },
                        { "NewGateway", data.NewGateway ?? "null" },
                        { "AdapterName", data.AdapterName ?? "null" }
                    },
                    success: true));

            return new { Success = true, Message = "IP updated." };
        }

        private async Task<object> HandleGetClockConfigCommand(UserSession session)
        {
            if (session.Role != UserRole.Admin)
                return new { Success = false, Message = "Permission denied." };

            return await _externalInterfacesManager.GetClockConfigurationAsync();
        }

        private async Task<object> HandleUpdateClockConfigCommand(UserSession session, DateTime newTime)
        {
            await _externalInterfacesManager.ProcessClockConfigurationAsync(newTime);

            // Publish admin activity event for audit logging
            await _eventAggregator.PublishAsync(
                new AdminActivityEvent(
                    "UpdateClockConfiguration",
                    session.Username,
                    "WebSocket",
                    new Dictionary<string, object> { { "NewTime", newTime.ToString("O") } },
                    success: true));

            return new { Success = true, Message = "Clock updated." };
        }

        private async Task<object> HandleSetPumpCurrentCommand(UserSession session, JsonElement payload)
        {
            if (session.Role != UserRole.Admin)
                return new { Success = false, Message = "Permission denied." };

            // Extract pump current value from payload
            if (!payload.TryGetProperty("CurrentMa", out var currentElement) ||
                (currentElement.ValueKind != JsonValueKind.Number))
            {
                return new { Success = false, Message = "Invalid payload: CurrentMa missing or not a number." };
            }

            double currentMa = currentElement.GetDouble();

            // Validate range (typical pump current range)
            if (currentMa < 0 || currentMa > 1000)
            {
                return new { Success = false, Message = "Pump current must be in range 0-1000 mA." };
            }

            _logger.LogInformation("User {Username} requested set pump current to {Current}mA",
                session.Username, currentMa);

            // Publish laser control event
            await _eventAggregator.PublishAsync(
                new LaserControlRequestedEvent(LaserControlType.SetPumpCurrent, currentMa, "WebSocket"));

            // Publish admin activity event for audit logging
            await _eventAggregator.PublishAsync(
                new AdminActivityEvent(
                    "SetPumpCurrent",
                    session.Username,
                    "WebSocket",
                    new Dictionary<string, object> { { "CurrentMa", currentMa } },
                    success: true));

            return new { Success = true, Message = $"Pump current set command sent: {currentMa}mA" };
        }

        private async Task<object> HandleSetPulseWidthCommand(UserSession session, JsonElement payload)
        {
            if (session.Role != UserRole.Admin)
                return new { Success = false, Message = "Permission denied." };

            // Extract pulse width value from payload
            if (!payload.TryGetProperty("PulseWidthNs", out var pulseWidthElement) ||
                (pulseWidthElement.ValueKind != JsonValueKind.Number))
            {
                return new { Success = false, Message = "Invalid payload: PulseWidthNs missing or not a number." };
            }

            int pulseWidthNs = pulseWidthElement.GetInt32();

            // Validate range (0-200 ns)
            if (pulseWidthNs < 0 || pulseWidthNs > 200)
            {
                return new { Success = false, Message = "Pulse width must be in range 0-200 ns." };
            }

            _logger.LogInformation("User {Username} requested set pulse width to {PulseWidth}ns",
                session.Username, pulseWidthNs);

            // Publish laser control event
            await _eventAggregator.PublishAsync(
                new LaserControlRequestedEvent(LaserControlType.SetPulseWidth, pulseWidthNs, "WebSocket"));

            // Publish admin activity event for audit logging
            await _eventAggregator.PublishAsync(
                new AdminActivityEvent(
                    "SetPulseWidth",
                    session.Username,
                    "WebSocket",
                    new Dictionary<string, object> { { "PulseWidthNs", pulseWidthNs } },
                    success: true));

            return new { Success = true, Message = $"Pulse width set command sent: {pulseWidthNs}ns" };
        }

        private object HandleUnknownCommand(UserSession session, string command)
        {
            return new { Success = false, Message = $"Unknown command: {command}" };
        }
    }
}
