using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using FireRakshak.FRMC.Core.Models;
using Microsoft.Extensions.Logging;

namespace FireRakshak.FRMC.Managers.SystemConfigurationManager;

/// <summary>
/// Handles saving and loading configuration data from JSON files.
/// </summary>
public class ConfigurationPersistence
{
    private readonly ILogger<ConfigurationPersistence> _logger;
    private readonly string _configDirectory = "./Config"; // TODO: Make configurable

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationPersistence"/> class.
    /// </summary>
    /// <param name="logger">The logger for recording information.</param>
    public ConfigurationPersistence(ILogger<ConfigurationPersistence> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(_configDirectory);
    }

    /// <summary>
    /// Saves a configuration object to a JSON file.
    /// </summary>
    /// <typeparam name="T">The type of the configuration object.</typeparam>
    /// <param name="config">The configuration object to save.</param>
    /// <param name="fileName">The name of the file (without extension).</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task SaveConfigurationAsync<T>(T config, string fileName)
    {
        var filePath = Path.Combine(_configDirectory, $"{fileName}.json");
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(filePath, json);
        _logger.LogInformation("Configuration saved to {FilePath}", filePath);
    }

    /// <summary>
    /// Loads a configuration object from a JSON file.
    /// </summary>
    /// <typeparam name="T">The type of the configuration object.</typeparam>
    /// <param name="fileName">The name of the file (without extension).</param>
    /// <returns>The loaded configuration object, or default if not found.</returns>
    public async Task<T?> LoadConfigurationAsync<T>(string fileName)
    {
        var filePath = Path.Combine(_configDirectory, $"{fileName}.json");
        if (File.Exists(filePath))
        {
            var json = await File.ReadAllTextAsync(filePath);
            _logger.LogInformation("Configuration loaded from {FilePath}", filePath);
            return JsonSerializer.Deserialize<T>(json);
        }
        _logger.LogWarning("Configuration file not found: {FilePath}", filePath);
        return default(T);
    }
}
