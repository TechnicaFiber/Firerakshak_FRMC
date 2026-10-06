using System;

namespace FireRakshak.FRMC.Core.Models
{
    public class AlarmIndex
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public AlarmThreshold Threshold { get; set; } = new AlarmThreshold();
    }
}
