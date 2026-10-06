using System.Collections.Generic;

namespace FireRakshak.FRMC.Core.Models
{
    /// <summary>
    /// Root object for ethconfig.json
    /// </summary>
    public class EthernetConfigFile
    {
        public List<EthernetConfiguration> EthernetPorts { get; set; }
            = new List<EthernetConfiguration>();
    }
}
