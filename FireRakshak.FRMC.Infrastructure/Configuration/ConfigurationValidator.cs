using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FireRakshak.FRMC.Infrastructure.Configuration
{

/// <summary>
/// Validates the application's configuration to ensure that all required settings
/// are present and have valid values. This helps to prevent runtime errors caused
/// by misconfiguration and provides clear feedback on startup if something is wrong.
/// </summary>
public class ConfigurationValidator
{
    private readonly ILogger<ConfigurationValidator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationValidator"/> class.
    /// </summary>
    /// <param name="logger">The logger for recording validation errors and information.</param>
    public ConfigurationValidator(ILogger<ConfigurationValidator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Validates a specific section of the application's configuration.
    /// </summary>
    /// <param name="configuration">The complete <see cref="IConfiguration"/> instance.</param>
    /// <param name="sectionName">The name (key) of the configuration section to validate.</param>
    /// <returns>True if the section exists and passes validation rules; otherwise, false.</returns>
    public bool ValidateSection(IConfiguration configuration, string sectionName)
    {
        _logger.LogInformation("Validating configuration section: {SectionName}", sectionName);
        var section = configuration.GetSection(sectionName);

        // Rule 1: The section must exist.
        if (!section.Exists())
        {
            _logger.LogError("Configuration validation failed: Section '{SectionName}' is missing.", sectionName);
            return false;
        }

        // TODO: Implement more specific validation rules for each section.
        // This could involve checking data types, ranges, or using a formal validation library like FluentValidation.
        
        // Example Rule 2: Check for a required key within a specific section.
        if (sectionName == "DtsConnection" && string.IsNullOrEmpty(section["IpAddress"])) 
        {
            _logger.LogError("Configuration validation failed: The 'IpAddress' key is missing in the 'DtsConnection' section.");
            return false;
        }
        
        _logger.LogInformation("Configuration section '{SectionName}' is valid.", sectionName);
        return true;
    }

    /// <summary>
    /// Performs a comprehensive validation of all critical sections of the application configuration.
    /// This method should be called during application startup to fail fast if the configuration is invalid.
    /// </summary>
    /// <param name="configuration">The complete <see cref="IConfiguration"/> instance.</param>
    /// <returns>True if all validated sections are valid; otherwise, false.</returns>
    public bool ValidateAll(IConfiguration configuration)
    {
        _logger.LogInformation("Validating all critical application configuration sections.");
        
        // Sequentially validate each required configuration section.
        // The '&=' operator ensures that all validations are executed, logging all potential errors.
        bool isValid = true;
        isValid &= ValidateSection(configuration, "DtsConnection");
        isValid &= ValidateSection(configuration, "ModbusServer");
        isValid &= ValidateSection(configuration, "WebSocketServers");
        isValid &= ValidateSection(configuration, "Logging");
        isValid &= ValidateSection(configuration, "BackgroundServices");
        
        // TODO: Add any other critical configuration sections that need to be validated.
        
        if (!isValid)
        {
            _logger.LogCritical("One or more configuration sections are invalid. The application may not function correctly.");
        }
        
        return isValid;
    }
}
}
