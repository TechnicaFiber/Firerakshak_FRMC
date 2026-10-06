using System.Security.Cryptography;
using System.Text.Json;

namespace FRMC.LoadTest;

/// <summary>
/// Utility to generate password hashes for FRMC users
/// </summary>
public static class PasswordUtil
{
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const int Iterations = 10000;

    public static string HashPassword(string password)
    {
        using var algorithm = new Rfc2898DeriveBytes(password, SaltSize, Iterations, HashAlgorithmName.SHA256);
        var salt = algorithm.Salt;
        var hash = algorithm.GetBytes(KeySize);
        return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    public static async Task ResetTestUser()
    {
        const string username = "testuser";
        const string password = "Test@123";
        const string usersFilePath = @"C:\FireRakshak\FinalTesting\Fire Rakshak Builds\Fire Rakshak\FRMC-SERIAL\FRMC-SERIAL\FireRakshak.FRMC.Host\Config\users.json";

        Console.WriteLine("╔═══════════════════════════════════════════════════════════╗");
        Console.WriteLine("║  FRMC User Password Reset Utility                         ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        // Generate password hash
        string hashedPassword = HashPassword(password);
        Console.WriteLine($"Generated hash for password '{password}':");
        Console.WriteLine($"  {hashedPassword}");
        Console.WriteLine();

        // Read existing users
        if (!File.Exists(usersFilePath))
        {
            Console.WriteLine($"❌ Users file not found: {usersFilePath}");
            return;
        }

        var usersJson = await File.ReadAllTextAsync(usersFilePath);
        var users = JsonSerializer.Deserialize<List<UserEntry>>(usersJson) ?? new List<UserEntry>();

        Console.WriteLine($"Found {users.Count} existing users:");
        foreach (var u in users)
        {
            Console.WriteLine($"  - {u.Username} (Role: {u.Role})");
        }
        Console.WriteLine();

        // Check if testuser exists
        var existingUser = users.FirstOrDefault(u => u.Username == username);
        if (existingUser != null)
        {
            existingUser.HashedPassword = hashedPassword;
            Console.WriteLine($"✅ Updated password for existing user '{username}'");
        }
        else
        {
            users.Add(new UserEntry
            {
                Username = username,
                HashedPassword = hashedPassword,
                Role = 1 // Admin role
            });
            Console.WriteLine($"✅ Created new user '{username}' with Admin role");
        }

        // Save updated users
        var options = new JsonSerializerOptions { WriteIndented = true };
        var newJson = JsonSerializer.Serialize(users, options);
        await File.WriteAllTextAsync(usersFilePath, newJson);

        Console.WriteLine();
        Console.WriteLine("════════════════════════════════════════════════════════════");
        Console.WriteLine($"User credentials for testing:");
        Console.WriteLine($"  Username: {username}");
        Console.WriteLine($"  Password: {password}");
        Console.WriteLine("════════════════════════════════════════════════════════════");
        Console.WriteLine();
        Console.WriteLine("⚠️  IMPORTANT: Restart FRMC for changes to take effect!");
    }

    private class UserEntry
    {
        public string Username { get; set; } = "";
        public string HashedPassword { get; set; } = "";
        public int Role { get; set; }
    }
}
