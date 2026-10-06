using System;
using System.Security.Cryptography;
using System.Text;

namespace FireRakshak.FRMC.Infrastructure.Security
{

/// <summary>
/// Provides strong password hashing and verification functionality.
/// It is crucial to never store plain-text passwords. This class ensures they are
/// stored securely using a modern, key-derivation algorithm.
/// </summary>
public class PasswordHasher
{
    // SaltSize: The size of the random salt to be generated. 16 bytes (128 bits) is a common and secure size.
    private const int SaltSize = 16; 
    // KeySize: The size of the hash to be generated. 32 bytes (256 bits) is standard for SHA256.
    private const int KeySize = 32;  
    // Iterations: The number of times the hashing algorithm is applied. A higher number increases the time
    // it takes to compute the hash, making brute-force attacks more difficult. 10,000 is a reasonable minimum.
    private const int Iterations = 10000; 

    /// <summary>
    /// Hashes a plain-text password using the PBKDF2 algorithm with a randomly generated salt.
    /// </summary>
    /// <param name="password">The plain-text password to hash.</param>
    /// <returns>
    /// A string containing the salt and the hashed password, formatted as "salt:hash".
    /// This combined format makes it easy to store and retrieve from a database.
    /// </returns>
    public string HashPassword(string password)
    {
        // Rfc2898DeriveBytes implements the PBKDF2 key-derivation function.
        using (var algorithm = new Rfc2898DeriveBytes(password, SaltSize, Iterations, HashAlgorithmName.SHA256))
        {
            var salt = algorithm.Salt; // The randomly generated salt.
            var hash = algorithm.GetBytes(KeySize); // The derived key (the hash).
            
            // Combine the salt and hash into a single string for storage.
            return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
        }
    }

    /// <summary>
    /// Verifies a plain-text password against a previously hashed password string.
    /// </summary>
    /// <param name="password">The plain-text password to verify.</param>
    /// <param name="hashedPassword">The stored hashed password string (in "salt:hash" format).</param>
    /// <returns>True if the password is correct; otherwise, false.</returns>
    public bool VerifyPassword(string password, string hashedPassword)
    {
        try
        {
            var parts = hashedPassword.Split(':');
            if (parts.Length != 2)
            {
                // The stored hash is not in the expected format.
                return false;
            }

            var salt = Convert.FromBase64String(parts[0]);
            var storedHash = Convert.FromBase64String(parts[1]);

            // Re-compute the hash of the provided password using the *same* salt and parameters.
            using (var algorithm = new Rfc2898DeriveBytes(password, salt, Iterations, HashAlgorithmName.SHA256))
            {
                var newHash = algorithm.GetBytes(KeySize);

                // Compare the newly computed hash with the stored hash.
                // It is important to use a constant-time comparison to prevent timing attacks,
                // where an attacker could measure the time it takes to compare the hashes.
                if (newHash.Length != storedHash.Length) return false;
                
                uint diff = (uint)newHash.Length ^ (uint)storedHash.Length;
                for (int i = 0; i < newHash.Length; i++)
                {
                    diff |= (uint)(newHash[i] ^ storedHash[i]);
                }
                return diff == 0;
            }
        }
        catch
        {
            // If any exception occurs (e.g., invalid Base64 string), the verification fails.
            return false;
        }
    }
}
}
