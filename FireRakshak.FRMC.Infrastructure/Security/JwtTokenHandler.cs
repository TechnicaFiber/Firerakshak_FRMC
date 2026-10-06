using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FireRakshak.FRMC.Infrastructure.Security
{

/// <summary>
/// Handles the generation and validation of JSON Web Tokens (JWT).
/// This is a core component for securing endpoints, particularly for WebSocket authentication,
/// by ensuring that clients are who they say they are.
/// </summary>
public class JwtTokenHandler
{
    private readonly ILogger<JwtTokenHandler> _logger;
    private readonly string _secret;

    /// <summary>
    /// Initializes a new instance of the <see cref="JwtTokenHandler"/> class.
    /// </summary>
    /// <param name="configuration">The application configuration, used to retrieve the JWT secret.</param>
    /// <param name="logger">The logger for recording token generation and validation events.</param>
    public JwtTokenHandler(IConfiguration configuration, ILogger<JwtTokenHandler> logger)
    {
        _logger = logger;
        // It is critical that the JWT Secret is configured and kept secure.
        // It should be stored in a secure location like Azure Key Vault or user secrets in development.
        _secret = configuration["Jwt:Secret"] ?? throw new InvalidOperationException("JWT Secret is not configured in appsettings.json. This is a critical security setting.");
    }

    /// <summary>
    /// Generates a JWT for a given username and role.
    /// The token contains claims that identify the user and their role.
    /// </summary>
    /// <param name="username">The username of the user.</param>
    /// <param name="role">The role assigned to the user (e.g., "Admin", "Operator").</param>
    /// <returns>A signed JWT token string.</returns>
    public string GenerateToken(string username, string role)
    {
        _logger.LogInformation("Generating JWT token for user: {Username}, role: {Role}", username, role);
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.ASCII.GetBytes(_secret);
        
        var tokenDescriptor = new SecurityTokenDescriptor
        {
            // The 'Subject' contains the claims of the token, such as username and role.
            Subject = new ClaimsIdentity(new[] 
            { 
                new Claim(ClaimTypes.Name, username), 
                new Claim(ClaimTypes.Role, role) 
            }),
            // The token's expiration time. After this time, the token will be invalid.
            Expires = DateTime.UtcNow.AddHours(1), // TODO: Make the expiration time configurable.
            // The signing credentials use a symmetric key and a strong hashing algorithm to sign the token.
            // This signature prevents the token from being tampered with.
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
        };
        
        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }

    /// <summary>
    /// Validates a JWT token string. It checks the signature and expiration time.
    /// </summary>
    /// <param name="token">The JWT token string to validate.</param>
    /// <returns>A <see cref="ClaimsPrincipal"/> containing the token's claims if the token is valid; otherwise, null.</returns>
    public ClaimsPrincipal? ValidateToken(string token)
    {
        _logger.LogInformation("Validating JWT token.");
        if (string.IsNullOrEmpty(token))
            return null;

        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.ASCII.GetBytes(_secret);
        try
        {
            var principal = tokenHandler.ValidateToken(token, new TokenValidationParameters
            {
                // ValidateIssuerSigningKey: Ensures that the token was signed with our secret key. This is critical.
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(key),

                // ValidateIssuer and ValidateAudience are set to false for simplicity.
                // In a multi-tenant or more complex application, you would want to validate
                // that the token was issued by a trusted authority (Issuer) and is intended for your application (Audience).
                ValidateIssuer = false, 
                ValidateAudience = false, 

                // ClockSkew = TimeSpan.Zero: Requires that the token's expiration time is exact.
                // A small ClockSkew can be used to account for time differences between servers.
                ClockSkew = TimeSpan.Zero 
            }, out SecurityToken validatedToken);

            return principal;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JWT token validation failed.");
            return null;
        }
    }
}
}
