using System.Collections.Generic;

namespace FireRakshak.FRMC.Core.Models;

public class NetworkAdapterOptions
{
    public Dictionary<string, string> AdapterMappings { get; set; } = new Dictionary<string, string>();
}
