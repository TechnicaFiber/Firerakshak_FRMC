using System;

namespace FireRakshak.FRMC.Core.Models
{
    public class AlarmThreshold
    {
        public bool UseMaxAlert1 { get; set; }
        public double MaxAlert1 { get; set; }

        public bool UseMaxAlert2 { get; set; }
        public double MaxAlert2 { get; set; }

        public bool UseMax { get; set; }
        public double Max { get; set; }

        public bool UseMin { get; set; }
        public double Min { get; set; }

        public bool UseRateOfChange { get; set; }
        public double RateOfChange { get; set; }

        public bool UseDeviation { get; set; }
        public double Deviation { get; set; }
    }
}
