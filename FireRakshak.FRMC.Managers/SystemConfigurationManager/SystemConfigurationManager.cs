using FireRakshak.FRMC.Core.Enums;
using FireRakshak.FRMC.Core.EventAggregator;
using FireRakshak.FRMC.Core.Events;
using FireRakshak.FRMC.Core.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace FireRakshak.FRMC.Managers.SystemConfigurationManager;

/// <summary>
/// Manages all system and DTS configuration operations, including persistence and auditing.
/// Publishes ConfigurationChangedEvent when settings are updated.
/// </summary>
public class SystemConfigurationManager : ISystemConfigurationManager
{
    private readonly IEventAggregator _eventAggregator;
    private readonly ILogger<SystemConfigurationManager> _logger;
    private readonly AuditTrailService _auditTrailService;
    private readonly ConfigurationPersistence _configurationPersistence;

    // In-memory configurations
    private SystemConfiguration _systemConfiguration;
    private DtsConfiguration _dtsConfiguration;

    /// <summary>
    /// Initializes a new instance of the <see cref="SystemConfigurationManager"/> class.
    /// </summary>
    /// <param name="eventAggregator">The event aggregator for publishing events.</param>
    /// <param name="logger">The logger for recording information.</param>
    /// <param name="auditTrailService">The audit trail service for logging configuration changes.</param>
    /// <param name="configurationPersistence">The configuration persistence service for saving/loading configurations.</param>
    public SystemConfigurationManager(IEventAggregator eventAggregator, ILogger<SystemConfigurationManager> logger,
                                      AuditTrailService auditTrailService, ConfigurationPersistence configurationPersistence)
    {
        _eventAggregator = eventAggregator;
        _logger = logger;
        _auditTrailService = auditTrailService;
        _configurationPersistence = configurationPersistence;

        // TODO: Load initial configurations from persistence
        _systemConfiguration = new SystemConfiguration("127.0.0.1", "255.255.255.0", "127.0.0.1", DateTime.UtcNow, "1.0.0", 5000);
        _dtsConfiguration = new DtsConfiguration(10, 1000, new List<byte> { 100, 100 });
    }

    /// <inheritdoc/>
    public Task<SystemConfiguration> GetSystemConfigurationAsync()
    {
        _logger.LogInformation("Retrieving system configuration.");
        return Task.FromResult(_systemConfiguration);
    }

    /// <inheritdoc/>
    public async Task UpdateSystemConfigurationAsync(SystemConfiguration config, string changedBy = "Admin")
    {
        _logger.LogInformation("Updating system configuration by {User}.", changedBy);
        // TODO: Compare old and new config to log specific changes
        var oldConfig = _systemConfiguration;
        _systemConfiguration = config;
        await _auditTrailService.LogConfigurationChangeAsync(ConfigurationType.System, changedBy, "All", oldConfig, config);
        await _eventAggregator.PublishAsync(
            new ConfigurationChangedEvent(
                ConfigurationType.System,
                changedBy,
                "FRMC",
                new Dictionary<string, object>()
            )
        );
        // TODO: Save to persistence
    }

    /// <inheritdoc/>
    public Task<DtsConfiguration> GetDtsConfigurationAsync()
    {
        _logger.LogInformation("Retrieving DTS configuration.");
        return Task.FromResult(_dtsConfiguration);
    }

    /// <inheritdoc/>
    public async Task UpdateDtsConfigurationAsync(DtsConfiguration config, string changedBy = "Admin")
    {
        _logger.LogInformation("Updating DTS configuration by {User}.", changedBy);
        // TODO: Compare old and new config to log specific changes
        var oldConfig = _dtsConfiguration;
        _dtsConfiguration = config;
        await _auditTrailService.LogConfigurationChangeAsync(ConfigurationType.Dts, changedBy, "All", oldConfig, config);
        await _eventAggregator.PublishAsync(
            new ConfigurationChangedEvent(
                ConfigurationType.Dts,
                changedBy,
                "FRMC",
                new Dictionary<string, object>()
            )
        );
        // TODO: Save to persistence
    }

    /// <inheritdoc/>
    public Task<List<ConfigurationChange>> GetAuditTrailAsync(DateTime from, DateTime to)
    {
        _logger.LogInformation("Retrieving audit trail from {From} to {To}", from, to);
        // TODO: Implement actual retrieval from _auditTrailService
        return Task.FromResult(new List<ConfigurationChange>());
    }
    public async Task<DtsConfiguration> LoadDtsConfigurationAsync()
    {
        var filePath = Path.Combine("Config", "dts_configuration.json");

        if (!File.Exists(filePath))
            throw new FileNotFoundException("DTS configuration file not found", filePath);

        var json = await File.ReadAllTextAsync(filePath);

        var config = JsonSerializer.Deserialize<DtsConfiguration>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (config == null)
            throw new InvalidOperationException("Failed to deserialize DTS configuration");

        return config;
    }
}

