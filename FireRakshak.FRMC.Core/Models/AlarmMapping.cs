using System;

namespace FireRakshak.FRMC.Core.Models
{
    public class AlarmMapping
    {
        public string Channel { get; set; } = "";
        public string ZoneName { get; set; } = "";
        public int RelayIndex { get; set; }
        public double StartMeter { get; set; }
        public double StopMeter { get; set; }
        public int AlarmIndexId { get; set; }
        public bool Active { get; set; }
    }
}
