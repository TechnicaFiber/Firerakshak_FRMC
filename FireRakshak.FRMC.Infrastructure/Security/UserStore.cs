using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FireRakshak.FRMC.Core.Enums;
using FireRakshak.FRMC.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace FireRakshak.FRMC.Infrastructure.Security {

    /// <summary>
    /// Manages user accounts, including their credentials and roles, for authentication and authorization purposes.
    /// This class acts as a repository for user data with file-based persistence.
    /// </summary>
    public class UserStore {
        private readonly PasswordHasher _passwordHasher;
        private readonly FileSystemPersistence _fileSystemPersistence;
        private readonly ILogger<UserStore> _logger;
        private readonly string _usersFilePath;

        // Thread-safe in-memory store backed by file persistence
        private readonly ConcurrentDictionary<string, UserAccount> _users = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="UserStore"/> class.
        /// </summary>
        /// <param name="passwordHasher">The password hasher service used to securely hash and verify passwords.</param>
        /// <param name="fileSystemPersistence">The file system persistence service for loading and saving users.</param>
        /// <param name="logger">The logger for recording user-related operations and events.</param>
        public UserStore(PasswordHasher passwordHasher, FileSystemPersistence fileSystemPersistence, ILogger<UserStore> logger) {
            _passwordHasher = passwordHasher;
            _fileSystemPersistence = fileSystemPersistence;
            _logger = logger;

            // Set users file path in Config directory
            _usersFilePath = Path.Combine("Config", "users.json");

            // Load users from file if it exists, otherwise create default users
            LoadUsersAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Synchronously adds a new user to the store after hashing their password.
        /// Used for initialization in constructor to avoid blocking.
        /// </summary>
        private void AddUser(string username, string password, UserRole role) {
            var hashedPassword = _passwordHasher.HashPassword(password);
            var userAccount = new UserAccount(username, hashedPassword, role);

            if (_users.TryAdd(username, userAccount))
            {
                _logger.LogInformation("User '{Username}' with role '{Role}' added to store.", username, role);
            }
            else
            {
                _logger.LogWarning("Attempted to add existing user '{Username}'.", username);
            }
        }

        /// <summary>
        /// Asynchronously adds a new user to the store after hashing their password and persists to file.
        /// </summary>
        /// <param name="username">The unique username for the new user.</param>
        /// <param name="password">The plain-text password for the new user.</param>
        /// <param name="role">The <see cref="UserRole"/> assigned to the new user.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task AddUserAsync(string username, string password, UserRole role) {
            var hashedPassword = _passwordHasher.HashPassword(password);
            var userAccount = new UserAccount(username, hashedPassword, role);

            // Attempt to add the user. If the username already exists, TryAdd will return false.
            if (_users.TryAdd(username, userAccount))
            {
                _logger.LogInformation("User '{Username}' with role '{Role}' added to store.", username, role);

                // Persist to file
                await SaveUsersAsync();
            }
            else
            {
                _logger.LogWarning("Attempted to add existing user '{Username}'.", username);
            }
        }

        /// <summary>
        /// Asynchronously validates user credentials by comparing the provided plain-text password
        /// with the stored hashed password for the given username.
        /// </summary>
        /// <param name="username">The username to validate.</param>
        /// <param name="password">The plain-text password to verify.</param>
        /// <returns>A task that represents the asynchronous operation. The task result is true if credentials are valid; otherwise, false.</returns>
        public Task<bool> ValidateCredentialsAsync(string username, string password) {
            if (_users.TryGetValue(username, out var userAccount)) {
                // Use the PasswordHasher to securely verify the password.
                return Task.FromResult(_passwordHasher.VerifyPassword(password, userAccount.HashedPassword));
            }
            _logger.LogWarning("Login attempt for non-existent user: '{Username}'.", username);
            return Task.FromResult(false);
        }

        /// <summary>
        /// Asynchronously retrieves a user's role based on their username.
        /// </summary>
        /// <param name="username">The username of the user.</param>
        /// <returns>
        /// A task that represents the asynchronous operation. The task result contains
        /// the <see cref="UserRole"/> of the user if found; otherwise, null.
        /// </returns>
        public Task<UserRole?> GetUserRoleAsync(string username) {
            if (_users.TryGetValue(username, out var userAccount)) {
                return Task.FromResult<UserRole?>(userAccount.Role);
            }
            _logger.LogWarning("Attempted to retrieve role for non-existent user: '{Username}'.", username);
            return Task.FromResult<UserRole?>(null);
        }

        /// <summary>
        /// Asynchronously deletes a user from the store and persists to file.
        /// </summary>
        /// <param name="username">The username of the user to delete.</param>
        /// <returns>A task that represents the asynchronous operation. The task result is true if user was deleted; otherwise, false.</returns>
        public async Task<bool> DeleteUserAsync(string username) {
            if (_users.TryRemove(username, out var removedUser))
            {
                _logger.LogInformation("User '{Username}' with role '{Role}' deleted from store.", username, removedUser.Role);

                // Persist to file
                await SaveUsersAsync();

                return true;
            }
            _logger.LogWarning("Attempted to delete non-existent user: '{Username}'.", username);
            return false;
        }

        /// <summary>
        /// Asynchronously retrieves all users from the store (without passwords).
        /// </summary>
        /// <returns>A task that represents the asynchronous operation. The task result contains a list of user information.</returns>
        public Task<System.Collections.Generic.List<UserInfo>> GetAllUsersAsync() {
            var userList = _users.Values.Select(u => new UserInfo
            {
                Username = u.Username,
                Role = u.Role
            }).ToList();

            return Task.FromResult(userList);
        }

        /// <summary>
        /// Checks if a user exists in the store.
        /// </summary>
        /// <param name="username">The username to check.</param>
        /// <returns>True if the user exists; otherwise, false.</returns>
        public bool UserExists(string username) {
            return _users.ContainsKey(username);
        }

        /// <summary>
        /// Asynchronously loads users from the persistent storage file.
        /// If the file doesn't exist, creates default admin and user accounts.
        /// </summary>
        private async Task LoadUsersAsync() {
            try
            {
                var fileContent = await _fileSystemPersistence.ReadFileAsync(_usersFilePath);

                if (fileContent != null)
                {
                    // Deserialize users from JSON
                    var persistedUsers = JsonSerializer.Deserialize<System.Collections.Generic.List<PersistedUser>>(fileContent);

                    if (persistedUsers != null && persistedUsers.Count > 0)
                    {
                        foreach (var persistedUser in persistedUsers)
                        {
                            var userAccount = new UserAccount(
                                persistedUser.Username,
                                persistedUser.HashedPassword,
                                persistedUser.Role
                            );

                            _users.TryAdd(persistedUser.Username, userAccount);
                        }

                        _logger.LogInformation("Loaded {Count} users from {FilePath}", persistedUsers.Count, _usersFilePath);
                        return;
                    }
                }

                // File doesn't exist or is empty - create default users
                _logger.LogWarning("⚠️ SECURITY WARNING: No persisted users found. Creating default users with DEFAULT PASSWORDS.");
                _logger.LogWarning("⚠️ Please change the default passwords immediately after first login!");
                _logger.LogWarning("⚠️ Default credentials: admin/adminpass, user/userpass");
                AddUser("admin", "adminpass", UserRole.Admin);
                AddUser("user", "userpass", UserRole.User);

                // Save default users to file
                await SaveUsersAsync();
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Error loading users from file. Creating default users.");

                // On error, ensure we have at least default users
                if (_users.IsEmpty)
                {
                    _logger.LogWarning("⚠️ SECURITY WARNING: Creating default users with DEFAULT PASSWORDS.");
                    _logger.LogWarning("⚠️ Please change the default passwords immediately after first login!");
                    AddUser("admin", "adminpass", UserRole.Admin);
                    AddUser("user", "userpass", UserRole.User);
                }
            }
        }

        /// <summary>
        /// Asynchronously saves all users to the persistent storage file.
        /// </summary>
        private async Task SaveUsersAsync() {
            try
            {
                // Convert ConcurrentDictionary to list for serialization
                var userList = _users.Values.Select(u => new PersistedUser
                {
                    Username = u.Username,
                    HashedPassword = u.HashedPassword,
                    Role = u.Role
                }).ToList();

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true
                };

                var json = JsonSerializer.Serialize(userList, options);
                await _fileSystemPersistence.WriteFileAsync(_usersFilePath, json);

                _logger.LogInformation("Saved {Count} users to {FilePath}", userList.Count, _usersFilePath);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Error saving users to file: {FilePath}", _usersFilePath);
                throw;
            }
        }

        /// <summary>
        /// Represents user information without sensitive data (for listing users).
        /// </summary>
        public class UserInfo {
            public string Username { get; set; } = string.Empty;
            public UserRole Role { get; set; }
        }

        /// <summary>
        /// Represents a persisted user account for JSON serialization/deserialization.
        /// </summary>
        private class PersistedUser {
            public string Username { get; set; } = string.Empty;
            public string HashedPassword { get; set; } = string.Empty;
            public UserRole Role { get; set; }
        }

        /// <summary>
        /// Represents a user account with their username, hashed password, and assigned role.
        /// </summary>
        private class UserAccount {
            public string Username { get; set; }
            public string HashedPassword { get; set; }
            public UserRole Role { get; set; }

            /// <summary>
            /// Initializes a new instance of the <see cref="UserAccount"/> class.
            /// </summary>
            /// <param name="username">The username.</param>
            /// <param name="hashedPassword">The securely hashed password.</param>
            /// <param name="role">The user's role.</param>
            public UserAccount(string username, string hashedPassword, UserRole role) {
                Username = username;
                HashedPassword = hashedPassword;
                Role = role;
            }
        }
    }
}
