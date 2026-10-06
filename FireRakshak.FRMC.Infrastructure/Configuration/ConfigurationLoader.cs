using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FireRakshak.FRMC.Infrastructure.Configuration
{

/// <summary>
/// Responsible for loading the application's configuration from various sources.
/// It builds a consolidated IConfiguration object that the application can use
/// to retrieve settings.
/// </summary>
public class ConfigurationLoader
{
    private readonly ILogger<ConfigurationLoader> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationLoader"/> class.
    /// </summary>
    /// <param name="logger">The logger for recording information about the configuration loading process.</param>
    public ConfigurationLoader(ILogger<ConfigurationLoader> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Builds and returns an <see cref="IConfiguration"/> instance by layering settings
    /// from multiple sources. The sources are layered in a specific order, with later
    /// sources overriding settings from earlier ones.
    /// </summary>
    /// <returns>A fully built <see cref="IConfiguration"/> instance.</returns>
    public IConfiguration LoadConfiguration()
    {
        _logger.LogInformation("Loading application configuration.");
        var builder = new ConfigurationBuilder()
            // Set the base path for resolving file-based configuration sources.
            .SetBasePath(Directory.GetCurrentDirectory())
            
            // 1. Load from the main appsettings.json file. This is non-optional.
            // reloadOnChange: true ensures the app can react to configuration changes without a restart.
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            
            // 2. Load from an environment-specific appsettings file (e.g., appsettings.Development.json).
            // This is optional and allows for overriding settings for different environments.
            .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json", optional: true, reloadOnChange: true)
            
            // 3. Load from environment variables. This is useful for containerized deployments
            // and for overriding settings in production without modifying files.
            .AddEnvironmentVariables();

        // TODO: If the application requires custom configuration files (e.g., for specific modules),
        // they can be added here using additional .AddJsonFile() or .AddXmlFile() calls.

        return builder.Build();
    }
}
}
