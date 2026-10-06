using System;

namespace FireRakshak.FRMC.Core.Models
{
    /// <summary>
    /// Represents configuration/state of a single Ethernet interface.
    /// Stored in ethconfig.json.
    /// </summary>
    public class EthernetConfiguration
    {
        /// <summary>
        /// Role identifier for this port (e.g. "NETRA1", "NETRA2")
        /// Used to identify which port to configure regardless of OS interface name
        /// </summary>
        public string Role { get; set; }

        /// <summary>
        /// OS interface name (e.g. "Ethernet", "Ethernet 2")
        /// This may change between boots - use MacAddress for stable identification
        /// </summary>
        public string InterfaceName { get; set; }

        /// <summary>
        /// MAC address used as stable hardware identity
        /// Format: "XX-XX-XX-XX-XX-XX" or "XX:XX:XX:XX:XX:XX"
        /// </summary>
        public string MacAddress { get; set; }

        /// <summary>
        /// IPv4 address
        /// </summary>
        public string Ip_Address { get; set; }

        /// <summary>
        /// Subnet mask
        /// </summary>
        public string SubnetMask { get; set; }

        /// <summary>
        /// Default gateway
        /// </summary>
        public string Gateway { get; set; }

        /// <summary>
        /// Whether DHCP is enabled on this interface
        /// </summary>
        public bool DhcpEnabled { get; set; }

        /// <summary>
        /// Last time this interface configuration was updated
        /// </summary>
        public DateTime LastUpdatedUtc { get; set; }

        // ✅ DEFAULT CONSTRUCTOR (JSON / SAFETY)
        public EthernetConfiguration()
        {
            Role = string.Empty;
            InterfaceName = string.Empty;
            MacAddress = string.Empty;
            Ip_Address = "0.0.0.0";
            SubnetMask = "255.255.255.0";
            Gateway = string.Empty;
            DhcpEnabled = true;
            LastUpdatedUtc = DateTime.UtcNow;
        }

        // ✅ PARAMETERIZED CONSTRUCTOR (OS / SET FLOW)
        public EthernetConfiguration(
            string role,
            string interfaceName,
            string macAddress,
            string ipAddress,
            string subnetMask,
            string gateway,
            bool dhcpEnabled)
        {
            Role = role;
            InterfaceName = interfaceName;
            MacAddress = macAddress;
            Ip_Address = ipAddress;
            SubnetMask = subnetMask;
            Gateway = gateway;
            DhcpEnabled = dhcpEnabled;
            LastUpdatedUtc = DateTime.UtcNow;
        }
    }
}
