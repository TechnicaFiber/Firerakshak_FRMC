using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace FireRakshak.FRMC.Managers.ServiceManager;

public class NetworkInterfaceService
{
    private readonly ILogger<NetworkInterfaceService>? _logger;

    public NetworkInterfaceService(ILogger<NetworkInterfaceService>? logger = null)
    {
        _logger = logger;
    }

    public List<EthernetInterfaceInfo> GetAvailableInterfaces()
    {
        var result = new List<EthernetInterfaceInfo>();

        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni =>
                ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                ni.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet ||
                ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            .ToList();

        foreach (var ni in interfaces)
        {
            var ipv4 = ni.GetIPProperties()
                .UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);

            result.Add(new EthernetInterfaceInfo
            {
                InterfaceName = ni.Name,
                Description = ni.Description,
                MacAddress = FormatMacAddress(ni.GetPhysicalAddress().ToString()),
                IpAddress = ipv4?.Address.ToString() ?? "Not assigned",
                Status = ni.OperationalStatus.ToString()
            });
        }

        return result;
    }

    public string? GetLiveIPv4(string interfaceName)
    {
        var ni = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n =>
                n.Name.Equals(interfaceName, StringComparison.OrdinalIgnoreCase));

        if (ni == null)
            return null;

        var ipv4 = ni.GetIPProperties()
            .UnicastAddresses
            .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);

        return ipv4?.Address.ToString();
    }

    public void ApplyStaticIp(
      string iface,
      string ip,
      string mask,
      string gateway,
      string dns)
    {
#if WINDOWS
    ApplyWindowsStaticIp(iface, ip, mask, gateway, dns);
#else 
        ApplyLinuxStaticIp(iface, ip, mask, gateway, dns);
#endif
    }


#if WINDOWS
private void ApplyWindowsStaticIp(
    string iface,
    string ip,
    string mask,
    string gateway,
    string dns)
{
    RunCommand("netsh",
        $"interface ip set address name=\"{iface}\" static {ip} {mask} {gateway}");

    RunCommand("netsh",
        $"interface ip set dns name=\"{iface}\" static {dns}");
}
#else

    private void ApplyLinuxStaticIp(
        string iface,
        string ip,
        string mask,
        string gateway,
        string dns)
    {
        _logger?.LogInformation("Applying Linux static IP to {Interface}: {IP}/{Mask}, Gateway: {Gateway}, DNS: {DNS}",
            iface, ip, mask, gateway, dns);

        int cidr = MaskToCidr(mask);

        // Try to find the connection name for this interface
        string conName = FindConnectionName(iface);
        _logger?.LogInformation("Resolved interface {Interface} to connection name {ConnectionName}", iface, conName);

        var modifyArgs = $"connection modify \"{conName}\" ipv4.method manual ipv4.addresses {ip}/{cidr}";

        if (!string.IsNullOrWhiteSpace(gateway))
            modifyArgs += $" ipv4.gateway {gateway}";
        else
            modifyArgs += " ipv4.gateway \"\"";

        if (!string.IsNullOrWhiteSpace(dns))
            modifyArgs += $" ipv4.dns {dns}";
        else
            modifyArgs += " ipv4.dns \"\"";

        RunCommand("sudo", $"nmcli {modifyArgs}");

        RunCommand("sudo", $"nmcli connection up \"{conName}\"");
    }

    private string FindConnectionName(string iface)
    {
        string output = RunCommand("nmcli", "-t -f NAME,DEVICE connection show");

        var lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var parts = line.Split(':');
            if (parts.Length < 2)
                continue;

            string first = parts[0].Trim();
            string second = parts[1].Trim();

            // Case 1: NAME:DEVICE
            if (second.Equals(iface, StringComparison.OrdinalIgnoreCase))
                return first;

            // Case 2: DEVICE:NAME
            if (first.Equals(iface, StringComparison.OrdinalIgnoreCase))
                return second;
        }

        _logger?.LogWarning(
            "Could not find nmcli connection for interface {Interface}, falling back to interface name",
            iface);

        return iface;
    }
#endif

    public void ApplyPersistentIp(string iface, string ip, string mask, string gateway)
    {
        _logger?.LogInformation("Applying Persistent IP to {Interface}: {IP}/{Mask}, Gateway: {Gateway}",
            iface, ip, mask, gateway);

        int cidr = MaskToCidr(mask);
        
        // Try to locate script in multiple potential locations
        string[] searchPaths = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scripts", "setup_ip.sh"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "setup_ip.sh"),
            "/usr/local/bin/setup_ip.sh"
        };

        string scriptPath = "";
        foreach (var path in searchPaths)
        {
            if (File.Exists(path))
            {
                scriptPath = path;
                break;
            }
        }

        if (string.IsNullOrEmpty(scriptPath))
        {
            string errorMsg = $"Critical: setup_ip.sh not found. Searched in: {string.Join(", ", searchPaths)}";
            _logger?.LogError(errorMsg);
            throw new FileNotFoundException(errorMsg);
        }

        // Ensure script is executable and has correct line endings (Linux only)
        if (!OperatingSystem.IsWindows())
        {
            _logger?.LogInformation("Cleaning line endings and executing script via bash: {Path}", scriptPath);
            
            try
            {
                // 1. Fix line endings (CRLF -> LF) directly in C# to avoid 'sed' quoting issues
                string content = File.ReadAllText(scriptPath);
                if (content.Contains("\r\n"))
                {
                    _logger?.LogInformation("Detected Windows line endings, converting to Linux format...");
                    File.WriteAllText(scriptPath, content.Replace("\r\n", "\n"));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to normalize line endings for {Path}. Continuing anyway...", scriptPath);
            }
            
            // 2. Ensure it's executable
            RunCommand("sudo", $"chmod +x \"{scriptPath}\"");
            
            // 3. Execute with bash
            RunCommand("sudo", $"bash \"{scriptPath}\" set \"{iface}\" \"{ip}\" \"{cidr}\" \"{gateway}\"");
        }
        else
        {
            _logger?.LogWarning("Persistent IP configuration via script is only supported on Linux.");
            RunCommand("netsh", $"interface ip set address name=\"{iface}\" static {ip} {mask} {gateway}");
        }
    }

    private static int MaskToCidr(string mask)
    {
        try
        {
            return mask.Split('.')
                .Select(octet => Convert.ToString(int.Parse(octet), 2))
                .Sum(binary => binary.Count(c => c == '1'));
        }
        catch (Exception)
        {
            return 24; // Default fallback
        }
    }
    public string? ResolveInterfaceByMac(string macAddress)
    {
        if (string.IsNullOrWhiteSpace(macAddress))
            return null;

        // Normalize MAC address format (remove separators and convert to uppercase)
        string normalizedTarget = NormalizeMac(macAddress);
        if (string.IsNullOrEmpty(normalizedTarget))
            return null;

        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
     .Where(ni =>
         ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
         ni.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet ||
         ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
     .ToList();


            foreach (var ni in interfaces)
            {
                var physicalAddress = ni.GetPhysicalAddress();
                string niMac = NormalizeMac(physicalAddress.ToString());

                if (string.Equals(niMac, normalizedTarget, StringComparison.OrdinalIgnoreCase))
                {
                    return ni.Name;
                }
            }
        }
        catch (Exception)
        {
            // Failed to enumerate interfaces
        }

        return null;
    }
    private static string NormalizeMac(string mac)
    {
        if (string.IsNullOrWhiteSpace(mac))
            return string.Empty;

        return mac
            .Replace("-", "")
            .Replace(":", "")
            .Replace(".", "")
            .ToUpperInvariant();
    }

    private string RunCommand(string fileName, string arguments)
    {
        _logger?.LogDebug("Running command: {FileName} {Arguments}", fileName, arguments);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(psi);
            if (process == null) return string.Empty;

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                _logger?.LogError("Command failed with exit code {ExitCode}. Error: {Error}", process.ExitCode, error);
                throw new Exception(error);
            }
            else if (!string.IsNullOrWhiteSpace(error))
            {
                _logger?.LogWarning("Command succeeded but had error output: {Error}", error);
            }

            return output.Trim();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Exception running command {FileName} {Arguments}", fileName, arguments);
            throw;
        }
    }

    private static string FormatMacAddress(string mac)
    {
        if (string.IsNullOrEmpty(mac) || mac.Length != 12)
            return mac;

        return string.Join("-", Enumerable.Range(0, 6).Select(i => mac.Substring(i * 2, 2)));
    }
}

public class EthernetInterfaceInfo
{
    public string InterfaceName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string MacAddress { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}