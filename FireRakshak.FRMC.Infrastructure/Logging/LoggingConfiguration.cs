using Serilog;
using Serilog.Events;
using Microsoft.Extensions.Hosting;
using System;
using System.IO;

namespace FireRakshak.FRMC.Infrastructure.Logging
{

/// <summary>
/// Provides extension methods for IHostBuilder to configure Serilog for structured logging.
/// This centralizes logging setup and ensures consistency across the application.
/// </summary>
public static class LoggingConfiguration
{
    // Log directory path - relative to application base directory
    private static readonly string LogDirectory = Path.Combine(AppContext.BaseDirectory, "Logs");

    /// <summary>
    /// Dynamic log level switch allowing runtime minimum log level changes from DTSCM without restart.
    /// </summary>
    public static readonly Serilog.Core.LoggingLevelSwitch LevelSwitch = new(LogEventLevel.Information);

    /// <summary>
    /// Dynamically sets the minimum logging severity level.
    /// </summary>
    /// <param name="levelStr">Log level string (Verbose, Debug, Information, Warning, Error, Fatal).</param>
    public static void SetMinimumLevel(string? levelStr)
    {
        if (string.IsNullOrWhiteSpace(levelStr)) return;

        if (Enum.TryParse<LogEventLevel>(levelStr, true, out var level))
        {
            LevelSwitch.MinimumLevel = level;
        }
    }

    /// <summary>
    /// Configures the Serilog logger with predefined settings for the application host.
    /// This method sets up various log sinks, minimum log levels, and enriches log events.
    /// </summary>
    /// <param name="hostBuilder">The <see cref="IHostBuilder"/> instance to configure.</param>
    /// <returns>The <see cref="IHostBuilder"/> instance for further configuration chaining.</returns>
    public static IHostBuilder ConfigureSerilog(this IHostBuilder hostBuilder)
    {
        // Ensure log directory exists
        Directory.CreateDirectory(LogDirectory);

        hostBuilder.UseSerilog((context, services, configuration) => configuration
            // Set the minimum level dynamically controlled by LevelSwitch (configurable from DTSCM)
            .MinimumLevel.ControlledBy(LevelSwitch)

            // Override minimum levels for specific namespaces to reduce noise from external libraries.
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)

            // Console sink: Information level and above
            .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Information)

            // File sink: All logs (Debug and above) - rolling daily
            // Location: {AppDir}/Logs/frmc-YYYYMMDD.log
            .WriteTo.File(
                Path.Combine(LogDirectory, "frmc-.log"),
                rollingInterval: RollingInterval.Day,
                restrictedToMinimumLevel: LogEventLevel.Debug,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                retainedFileCountLimit: 30,  // Keep 30 days of logs
                fileSizeLimitBytes: 100_000_000,  // 100MB max per file
                rollOnFileSizeLimit: true)

            // Separate file for errors only - easier to find critical issues
            // Location: {AppDir}/Logs/frmc-errors-YYYYMMDD.log
            .WriteTo.File(
                Path.Combine(LogDirectory, "frmc-errors-.log"),
                rollingInterval: RollingInterval.Day,
                restrictedToMinimumLevel: LogEventLevel.Error,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                retainedFileCountLimit: 90)  // Keep 90 days of error logs

            // Enrich log events with contextual information
            .Enrich.FromLogContext()
        );

        return hostBuilder;
    }
}
}
