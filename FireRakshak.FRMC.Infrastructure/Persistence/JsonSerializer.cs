using System.Text.Json;
using System.Text.Json.Serialization;

namespace FireRakshak.FRMC.Infrastructure.Persistence
{

/// <summary>
/// Provides a centralized location for defining custom JSON serialization settings.
/// Using a shared configuration ensures that all JSON serialization and deserialization
/// throughout the application is consistent.
/// </summary>
public static class JsonSerializerSettings
{
    /// <summary>
    /// Gets the default <see cref="JsonSerializerOptions"/> for the application.
    /// These options are configured for readability and web-standard compatibility.
    /// </summary>
    public static JsonSerializerOptions DefaultOptions => new JsonSerializerOptions
    {
        // WriteIndented = true: Makes the serialized JSON human-readable by adding indentation.
        // This is useful for debugging and for configuration files.
        WriteIndented = true,

        // PropertyNamingPolicy = JsonNamingPolicy.CamelCase: Converts property names from PascalCase (e.g., MyProperty)
        // to camelCase (e.g., myProperty), which is the standard convention for JSON.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,

        // PropertyNameCaseInsensitive = true: Allows deserialization to match camelCase JSON properties
        // to PascalCase C# properties. Essential for reading back configuration files that were saved
        // using camelCase naming policy.
        PropertyNameCaseInsensitive = true,

        // TODO: Add more custom settings or converters as the application evolves.
        // For example, you might add a custom converter for specific enums or complex types.
        // Converters = { new JsonStringEnumConverter() }
    };
}
}
