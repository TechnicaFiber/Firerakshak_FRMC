using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace FireRakshak.FRMC.Core.Models
{
    /// <summary>
    /// Represents configuration settings for the FRMC device.
    /// </summary>
    public class FrmcConfiguration
    {
        public string DeviceId { get; set; }
        public string IpAddress { get; set; }
        public int Port { get; set; }
        public bool IsClockSyncEnabled { get; set; }
        public DateTime LastUpdated { get; set; }
        public Dictionary<string, object> AdditionalSettings { get; set; }

        // ✅ Added for SystemConfiguration compatibility
        public string SubnetMask { get; set; }
        public string Gateway { get; set; }
        public string SoftwareVersion { get; set; }
        public int BackgroundServiceInterval { get; set; }

        // ✅ Added for WebSocket Security
        public bool UseSecure { get; set; }
        public string CertificatePath { get; set; }
        public string CertificatePassword { get; set; }

        /// <summary>
        /// Alias for compatibility with older code.
        /// </summary>
        public string CurrentIP => IpAddress;

        /// <summary>
        /// Default constructor for deserialization and initialization.
        /// </summary>
        public FrmcConfiguration()
        {
            DeviceId = "FRMC-001";
            IpAddress = CurrentIP;
            Port = 5000;
            IsClockSyncEnabled = true;
            LastUpdated = DateTime.UtcNow;
            AdditionalSettings = new Dictionary<string, object>();

            // ✅ Default values for new properties
            SubnetMask = "255.255.255.0";
            Gateway = "192.168.1.1";
            SoftwareVersion = "1.0.0";
            BackgroundServiceInterval = 5000;

            UseSecure = true;
            CertificatePath = "localhost.pfx";
            CertificatePassword = "FRMC@123";
        }

        /// <summary>
        /// Parameterized constructor for manual initialization.
        /// </summary>
        public FrmcConfiguration(string deviceId, string ipAddress, int port, bool isClockSyncEnabled)
        {
            DeviceId = deviceId;
            IpAddress = ipAddress;
            Port = port;
            IsClockSyncEnabled = isClockSyncEnabled;
            LastUpdated = DateTime.UtcNow;
            AdditionalSettings = new Dictionary<string, object>();

            // ✅ Default values for new properties
            SubnetMask = "255.255.255.0";
            Gateway = "192.168.1.1";
            SoftwareVersion = "1.0.0";
            BackgroundServiceInterval = 5000;

            UseSecure = false;
            CertificatePath = "";
            CertificatePassword = "";
        }

        public void UpdateSetting(string key, object value)
        {
            if (AdditionalSettings.ContainsKey(key))
                AdditionalSettings[key] = value;
            else
                AdditionalSettings.Add(key, value);

            LastUpdated = DateTime.UtcNow;
        }

        public object? GetSetting(string key)
        {
            return AdditionalSettings.TryGetValue(key, out var value) ? value : null;
        }

        public void SaveToFile(string filePath)
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(filePath, json);
        }

        public static FrmcConfiguration? LoadFromFile(string filePath)
        {
            if (!File.Exists(filePath)) return null;

            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<FrmcConfiguration>(json);
        }
    }
}
