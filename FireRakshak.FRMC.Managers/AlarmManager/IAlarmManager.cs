using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FireRakshak.FRMC.Core.Models;

namespace FireRakshak.FRMC.Managers.AlarmManager;

/// <summary>
/// Defines the contract for managing alarms within the fire detection system.
/// This includes configuring alarm conditions, querying active alarms, and clearing them.
/// </summary>
public interface IAlarmManager
{
    /// <summary>
    /// Asynchronously retrieves the configuration for a specific alarm.
    /// </summary>
    /// <param name="alarmId">The unique identifier for the alarm.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. 
    /// The task result contains the <see cref="AlarmConfiguration"/> for the specified alarm.
    /// </returns>
    Task<AlarmConfiguration> GetAlarmConfigurationAsync(int alarmId);

    /// <summary>
    /// Asynchronously updates the configuration for a specific alarm.
    /// </summary>
    /// <param name="config">The <see cref="AlarmConfiguration"/> object with the updated settings.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task UpdateAlarmConfigurationAsync(AlarmConfiguration config);

    /// <summary>
    /// Asynchronously retrieves a list of all currently active alarms in the system.
    /// </summary>
    /// <returns>
    /// A task that represents the asynchronous operation. 
    /// The task result contains a list of <see cref="ActiveAlarm"/> objects.
    /// </returns>
    Task<List<ActiveAlarm>> GetActiveAlarmsAsync();

    /// <summary>
    /// Asynchronously clears a specific active alarm.
    /// This would typically be done after an operator has acknowledged and resolved the alarm condition.
    /// </summary>
    /// <param name="alarmId">The unique identifier of the alarm to clear.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task ClearAllAlarmsAsync();
    Task ClearAlarmAsync(int alarmId);

    /// <summary>
    /// Gets active alarms for a specific channel and optionally a specific zone.
    /// </summary>
    /// <param name="channelId">The channel ID to filter by.</param>
    /// <param name="zoneId">Optional zone ID to filter by. If null, returns all alarms for the channel.</param>
    /// <returns>List of active alarms matching the filter criteria.</returns>
    Task<List<ActiveAlarm>> GetActiveAlarmsForChannelAsync(int channelId, int? zoneId = null);
}
