using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FireRakshak.FRMC.Core.Models;

namespace FireRakshak.FRMC.Managers.SystemConfigurationManager;

/// <summary>
/// Defines the contract for managing all system and DTS configurations.
/// </summary>
public interface ISystemConfigurationManager
{
    /// <summary>
    /// Retrieves the overall system configuration.
    /// </summary>
    /// <returns>The SystemConfiguration object.</returns>
    Task<SystemConfiguration> GetSystemConfigurationAsync();

    /// <summary>
    /// Updates the overall system configuration.
    /// </summary>
    /// <param name="config">The new SystemConfiguration.</param>
    /// <param name="changedBy">The user who changed the configuration.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task UpdateSystemConfigurationAsync(SystemConfiguration config, string changedBy = "Admin");

    /// <summary>
    /// Retrieves the DTS specific configuration.
    /// </summary>
    /// <returns>The DtsConfiguration object.</returns>
    Task<DtsConfiguration> GetDtsConfigurationAsync();

    /// <summary>
    /// Updates the DTS specific configuration.
    /// </summary>
    /// <param name="config">The new DtsConfiguration.</param>
    /// <param name="changedBy">The user who changed the configuration.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task UpdateDtsConfigurationAsync(DtsConfiguration config, string changedBy = "Admin");

    /// <summary>
    /// Retrieves an audit trail of configuration changes within a specified time range.
    /// </summary>
    /// <param name="from">The start date and time for the audit trail.</param>
    /// <param name="to">The end date and time for the audit trail.</param>
    /// <returns>A list of ConfigurationChange objects.</returns>
    Task<List<ConfigurationChange>> GetAuditTrailAsync(DateTime from, DateTime to);
}

/// <summary>
/// Represents a single configuration change entry in the audit trail.
/// </summary>
public record ConfigurationChange(DateTime Timestamp, string ChangedBy, string ConfigurationType, string Key, object OldValue, object NewValue);
