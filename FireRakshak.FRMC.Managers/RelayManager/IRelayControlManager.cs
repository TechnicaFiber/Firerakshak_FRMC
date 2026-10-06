using System.Collections.Generic;
using System.Threading.Tasks;
using FireRakshak.FRMC.Core.Models;

namespace FireRakshak.FRMC.Managers.RelayManager
{
    /// <summary>
    /// Defines the contract for managing relay control operations in the fire detection system.
    /// This includes manual and automatic relay control and state management.
    ///
    /// NOTE: Zone-to-relay assignments are tracked in ZoneConfiguration.AssignedRelayId,
    /// not in this manager, to support 1:many relationships (multiple zones per relay).
    /// </summary>
    public interface IRelayControlManager
    {
        /// <summary>
        /// Asynchronously turns a relay ON.
        /// </summary>
        /// <param name="relayId">The relay identifier (1-based).</param>
        /// <param name="source">The control source (Auto or Manual).</param>
        /// <param name="user">The username or system identifier requesting the change.</param>
        /// <returns>True if the command was successful, false otherwise.</returns>
        Task<bool> TurnRelayOnAsync(int relayId, RelayControlSource source, string user);

        /// <summary>
        /// Asynchronously turns a relay OFF.
        /// </summary>
        /// <param name="relayId">The relay identifier (1-based).</param>
        /// <param name="source">The control source (Auto or Manual).</param>
        /// <param name="user">The username or system identifier requesting the change.</param>
        /// <returns>True if the command was successful, false otherwise.</returns>
        Task<bool> TurnRelayOffAsync(int relayId, RelayControlSource source, string user);

        /// <summary>
        /// Asynchronously retrieves the current state of a specific relay.
        /// </summary>
        /// <param name="relayId">The relay identifier (1-based).</param>
        /// <returns>The current RelayState, or null if the relay doesn't exist.</returns>
        Task<RelayState?> GetRelayStateAsync(int relayId);

        /// <summary>
        /// Asynchronously retrieves the states of all relays in the system.
        /// </summary>
        /// <returns>A list of all RelayState objects.</returns>
        Task<List<RelayState>> GetAllRelayStatesAsync();

        /// <summary>
        /// Gets the list of relay IDs available for zone assignment.
        /// Excludes fixed/reserved relays (1-11) and already assigned relays.
        /// </summary>
        /// <param name="excludeChannelId">Optional: Channel ID of zone being edited (for reassignment)</param>
        /// <param name="excludeZoneId">Optional: Zone ID of zone being edited (for reassignment)</param>
        /// <returns>List of available relay IDs (12 and above)</returns>
        List<int> GetAvailableRelays(int? excludeChannelId = null, int? excludeZoneId = null);

        /// <summary>
        /// Turns on the System Running relay (Relay 1). Called when system starts.
        /// </summary>
        Task SetSystemRunningRelayAsync(bool isRunning);

        /// <summary>
        /// Sets the System Health Fault relay (Relay 2). Called when system health issues occur.
        /// </summary>
        Task SetSystemHealthFaultRelayAsync(bool hasFault);

        /// <summary>
        /// Sets the Common Alarm relay (Relay 3). Called when any alarm is active in the system.
        /// </summary>
        Task SetCommonAlarmRelayAsync(bool hasAlarm);

        /// <summary>
        /// Initializes or re-initializes the relay pool with the specified number of relays.
        /// Called after system configuration changes.
        /// </summary>
        /// <param name="relayCount">Number of relays to initialize (typically from SystemConfiguration.NumberOfRelays)</param>
        void InitializeRelays(int relayCount);


        /// <summary>
        /// Loads relay states from persistent storage. Called after InitializeRelays to restore previous states.
        /// </summary>
        Task LoadRelayStatesAsync();

        /// <summary>
        /// Turns off any zone-assigned relays (12+) that are currently ON but no longer assigned to any zone.
        /// Called after zone configuration changes (relay reassignment / unassignment).
        /// </summary>
        Task CleanupUnassignedRelaysAsync();

        /// <summary>
        /// Clears all relays on startup by sending RS232 OFF commands.
        /// Skips Relay 1 (System Running) which is handled separately.
        /// Ensures hardware relay state matches clean in-memory state after restart.
        /// </summary>
        Task ClearAllRelaysOnStartupAsync();

    }
}