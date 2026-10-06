namespace FireRakshak.FRMC.Core.Models;

public class SystemIpConfigurationPayload
{
    public string? NewIpAddress { get; set; }
    public string? NewSubnetMask { get; set; }
    public string? NewGateway { get; set; }
    public string? AdapterName { get; set; }
}
