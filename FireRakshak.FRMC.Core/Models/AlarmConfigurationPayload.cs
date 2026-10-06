using System.Collections.Generic;

namespace FireRakshak.FRMC.Core.Models
{
    public class AlarmConfigurationPayload
    {
        public List<AlarmIndex> Indices { get; set; } = new();
        public List<AlarmMapping> Mappings { get; set; } = new();
    }
}
