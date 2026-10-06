using System;
using Microsoft.Extensions.Logging;

namespace FireRakshak.FRMC.Infrastructure.Logging
{

/// <summary>
/// A generic wrapper around <see cref="ILogger{T}"/> that can be used to enforce structured logging
/// conventions or add common properties to log events. This allows for consistent logging practices
/// across different parts of the application.
/// </summary>
/// <typeparam name="T">The type whose name is used for the logger category.</typeparam>
public class StructuredLogger<T> : ILogger<T>
{
    private readonly ILogger<T> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StructuredLogger{T}"/> class.
    /// </summary>
    /// <param name="logger">The underlying <see cref="ILogger{T}"/> instance that this wrapper delegates to.</param>
    public StructuredLogger(ILogger<T> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    /// <summary>
    /// Begins a logical operation scope. This allows for grouping related log messages.
    /// </summary>
    /// <param name="state">The state object for the scope.</param>
    /// <typeparam name="TState">The type of the state object.</typeparam>
    /// <returns>An <see cref="IDisposable"/> that ends the logical operation scope when disposed.</returns>
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return _logger.BeginScope(state)!;
    }

    /// <inheritdoc/>
    /// <summary>
    /// Checks if the given <paramref name="logLevel"/> is enabled.
    /// </summary>
    /// <param name="logLevel">The <see cref="LogLevel"/> to check.</param>
    /// <returns>True if the <paramref name="logLevel"/> is enabled; otherwise, false.</returns>
    public bool IsEnabled(LogLevel logLevel)
    {
        return _logger.IsEnabled(logLevel);
    }

    /// <inheritdoc/>
    /// <summary>
    /// Writes a log entry.
    /// </summary>
    /// <param name="logLevel">Entry will be written on this level.</param>
    /// <param name="eventId">Id of the event.</param>
    /// <param name="state">The entry to be written. Can be also an object.</param>
    /// <param name="exception">The exception related to this entry.</param>
    /// <param name="formatter">Function to create a <see cref="string"/> message of the <paramref name="state"/> and <paramref name="exception"/>.</param>
    /// <typeparam name="TState">The type of the object to be written.</typeparam>
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        _logger.Log(logLevel, eventId, state, exception, formatter);
    }
}
}
