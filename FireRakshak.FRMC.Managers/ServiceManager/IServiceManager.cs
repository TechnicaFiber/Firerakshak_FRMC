using System;
using System.Threading.Tasks;
using FireRakshak.FRMC.Core.Enums;

namespace FireRakshak.FRMC.Managers.ServiceManager;

/// <summary>
/// Defines the contract for managing user authentication and command processing.
/// </summary>
public interface IServiceManager
{
    /// <summary>
    /// Authenticates a user with the provided username and password.
    /// </summary>
    /// <param name="username">The user's username.</param>
    /// <param name="password">The user's password.</param>
    /// <returns>True if authentication is successful, false otherwise.</returns>
    Task<bool> AuthenticateUserAsync(string username, string password);

    /// <summary>
    /// Creates a user session for an authenticated user.
    /// </summary>
    /// <param name="username">The username of the authenticated user.</param>
    /// <param name="role">The role of the authenticated user.</param>
    /// <returns>A UserSession object.</returns>
    Task<UserSession> CreateSessionAsync(string username, UserRole role);

    /// <summary>
    /// Processes a command from a user session.
    /// </summary>
    /// <param name="session">The active user session.</param>
    /// <param name="command">The command string to process.</param>
    /// <param name="payload">Optional payload associated with the command.</param>
    /// <returns>A task representing the asynchronous operation, returning an object as a response payload.</returns>
    Task<object> ProcessCommandAsync(UserSession session, string command, System.Text.Json.JsonElement payload);
}

/// <summary>
/// Represents an active user session.
/// </summary>
public record UserSession(string SessionId, string Username, UserRole Role, DateTime CreatedAt);
