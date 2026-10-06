using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FireRakshak.FRMC.Core.Models;
using FireRakshak.FRMC.Host;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FireRakshak.FRMC.Managers.AlarmManager;

/// <summary>
/// Background service for periodically publishing alarm states.
/// Provides periodic state broadcasts for redundancy, late-joining clients, and external monitoring systems.
/// </summary>
public class AlarmPublisherService : BackgroundService
{
    private readonly IAlarmManager _alarmManager;
    private readonly WebSocketServer _wsServer;
    private readonly ILogger<AlarmPublisherService> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(1); // Publish every 1 minute

    /// <summary>
    /// Initializes a new instance of the <see cref="AlarmPublisherService"/> class.
    /// </summary>
    /// <param name="alarmManager">The alarm manager for retrieving active alarms.</param>
    /// <param name="wsServer">The WebSocket server for broadcasting alarm status.</param>
    /// <param name="logger">The logger for recording information.</param>
    public AlarmPublisherService(
        IAlarmManager alarmManager,
        WebSocketServer wsServer,
        ILogger<AlarmPublisherService> logger)
    {
        _alarmManager = alarmManager;
        _wsServer = wsServer;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Alarm Publisher Service started. Broadcasting active alarms every {Interval} seconds.", _interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Get all active alarms from AlarmManager
                var activeAlarms = await _alarmManager.GetActiveAlarmsAsync();

                // Build consolidated active alarms payload
                var alarmsPayload = activeAlarms.Select(a => new
                {
                    AlarmId = a.AlarmId,
                    ChannelId = a.ChannelId,
                    ZoneId = a.ZoneId,
                    AlarmType = a.Type.ToString(),
                    TriggerValue = a.TriggerValue,
                    TriggeredAt = a.TriggeredAt,
                    ZoneName = a.ZoneStateSnapshot?.Name ?? $"Zone {a.ZoneId}",
                    Message = $"Active: Ch{a.ChannelId}/Zone{a.ZoneId} - {a.Type} ({a.TriggerValue}°C)"
                }).ToList();

                var payload = new
                {
                    Timestamp = DateTime.UtcNow,
                    TotalActiveAlarms = activeAlarms.Count,
                    ActiveAlarms = alarmsPayload
                };

                // Broadcast consolidated alarm status to all WebSocket clients
                // This provides periodic state updates for:
                // - Redundancy (re-broadcast in case individual alarm messages were missed)
                // - Late joiners (clients that connected after alarms were triggered)
                // - External monitoring systems (periodic polling instead of event-driven)
                var wsMessage = new WebSocketMessage(
                    "active_alarms_status",
                    JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement
                );

                await _wsServer.BroadcastMessageAsync(wsMessage);

                if (activeAlarms.Count > 0)
                {
                    _logger.LogDebug("Published {AlarmCount} active alarms to external interfaces.", activeAlarms.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error publishing active alarms.");
            }

            await Task.Delay(_interval, stoppingToken);
        }

        _logger.LogInformation("Alarm Publisher Service stopped.");
    }
}
