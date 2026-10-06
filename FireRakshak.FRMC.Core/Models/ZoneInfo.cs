//namespace FireRakshak.FRMC.Core.Models
//{
//    public class ZoneInfo
//    {
//        public string ZoneId { get; set; }        // e.g. "1.3"
//        public string Name { get; set; } = "";

//        public double StartPoint { get; set; }
//        public double EndPoint { get; set; }
//        public double MaxTemp { get; set; }
//        public double MinTemp { get; set; }
//        public double PreAlarm { get; set; }
//        public double RoRThreshold { get; set; }
//        public double DeviationThreshold { get; set; }

//        public bool Enabled { get; set; }
//        public bool IsMaxTempEnabled { get; set; }
//        public bool IsMinTempEnabled { get; set; }
//        public bool IsPreAlarmEnabled { get; set; }
//        public bool IsRateOfRiseEnabled { get; set; }
//        public bool IsDeviationEnabled { get; set; }

//    }
//}
using System.Text.Json.Serialization;

namespace FireRakshak.FRMC.Core.Models
{
    public class ZoneInfo
    {
        public int ZoneId { get; set; }        // e.g. "1"
        public string Name { get; set; } = "";

        public double StartPoint { get; set; }
        public double EndPoint { get; set; }
        public double MaxTemp { get; set; }
        public double MinTemp { get; set; }
        public double PreAlarm { get; set; }
        public double RoRThreshold { get; set; }
        public double DeviationThreshold { get; set; }

        public bool Enabled { get; set; }

        [JsonPropertyName("isMaxTempEnabled")]
        public bool IsMaxTempEnabled { get; set; }

        [JsonPropertyName("isMinTempEnabled")]
        public bool IsMinTempEnabled { get; set; }

        [JsonPropertyName("isPreAlarmEnabled")]
        public bool IsPreAlarmEnabled { get; set; }

        [JsonPropertyName("isRateOfRiseEnabled")]
        public bool IsRateOfRiseEnabled { get; set; }

        [JsonPropertyName("isDeviationEnabled")]
        public bool IsDeviationEnabled { get; set; }

        [JsonPropertyName("assignedRelayId")]
        public int? AssignedRelayId { get; set; }
    }
}
