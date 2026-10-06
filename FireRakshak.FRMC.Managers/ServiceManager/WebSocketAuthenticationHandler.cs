using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using FireRakshak.FRMC.Infrastructure.Security;

namespace FireRakshak.FRMC.Managers.ServiceManager;

/// <summary>
/// Handles WebSocket connection authentication using JWT tokens.
/// </summary>
public class WebSocketAuthenticationHandler
{
    private readonly JwtTokenHandler _jwtTokenHandler;
    private readonly IServiceManager _serviceManager;
    private readonly ILogger<WebSocketAuthenticationHandler> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebSocketAuthenticationHandler"/> class.
    /// </summary>
    /// <param name="jwtTokenHandler">The JWT token handler for token validation.</param>
    /// <param name="serviceManager">The service manager for user authentication and session creation.</param>
    /// <param name="logger">The logger for recording information.</param>
    public WebSocketAuthenticationHandler(JwtTokenHandler jwtTokenHandler, IServiceManager serviceManager, ILogger<WebSocketAuthenticationHandler> logger)
    {
        _jwtTokenHandler = jwtTokenHandler;
        _serviceManager = serviceManager;
        _logger = logger;
    }

    /// <summary>
    /// Authenticates a WebSocket connection based on a JWT token provided in the query string.
    /// </summary>
    /// <param name="authToken">The JWT token string to validate.</param>
    /// <returns>A UserSession if authentication is successful, otherwise null.</returns>
    public async Task<UserSession?> AuthenticateWebSocketAsync(string? authToken)
    {
        _logger.LogInformation("Attempting to authenticate WebSocket connection.");

        if (string.IsNullOrEmpty(authToken))
        {
            _logger.LogWarning("WebSocket authentication failed: No token provided.");
            return null;
        }

        var principal = _jwtTokenHandler.ValidateToken(authToken);
        if (principal == null)
        {
            _logger.LogWarning("WebSocket authentication failed: Invalid token.");
            return null;
        }

        if (principal.Identity == null || string.IsNullOrEmpty(principal.Identity.Name))
        {
            _logger.LogWarning("WebSocket authentication failed: User identity not found in token.");
            return null;
        }

        var username = principal.Identity.Name;

        // Extract role from claims
        var roleClaim = principal.FindFirst(System.Security.Claims.ClaimTypes.Role);
        var role = Core.Enums.UserRole.User; // Default to User role

        if (roleClaim != null && Enum.TryParse<Core.Enums.UserRole>(roleClaim.Value, out var parsedRole))
        {
            role = parsedRole;
        }

        _logger.LogInformation("WebSocket authenticated for user: {Username}, Role: {Role}", username, role);
        return await _serviceManager.CreateSessionAsync(username, role);
    }
}
