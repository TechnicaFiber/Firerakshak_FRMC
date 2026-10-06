using FireRakshak.FRMC.Core.Models;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace FireRakshak.FRMC.Managers.SystemHealthManager;

/// <summary>
/// Defines the contract for managing and monitoring the overall health of the FRMC system.
/// This includes aggregating health information from various components like the DTS device,
/// communication links, and the host system itself.
/// </summary>
public interface ISystemHealthManager
{
    /// <summary>
    /// Asynchronously retrieves the current aggregated health parameters of the system.
    /// </summary>
    /// <returns>
    /// A task that represents the asynchronous operation.
    /// The task result contains the current <see cref="HealthParameters"/> of the system.
    /// </returns>
    Task<HealthParameters> GetCurrentHealthAsync();

    /// <summary>
    /// Asynchronously updates the system's health parameters. This method is typically called
    /// by monitoring services that check the status of different system components.
    /// </summary>
    /// <param name="health">The <see cref="HealthParameters"/> object containing the updated values.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task UpdateHealthParametersAsync(HealthParameters health);
    JsonElement CurrentHealthPayload { get; }

    /// <summary>
    /// Asynchronously retrieves all active system health faults.
    /// </summary>
    /// <returns>
    /// A task that represents the asynchronous operation.
    /// The task result contains a list of active <see cref="SystemHealthFault"/> objects.
    /// </returns>
    Task<List<SystemHealthFault>> GetActiveHealthFaultsAsync();
    bool IsHealthBad();
}
