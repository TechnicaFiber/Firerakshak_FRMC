using System;
using System.Collections.Generic;

namespace FireRakshak.FRMC.Core.Models
{
    /// <summary>
    /// Represents the current operational state of a zone.
    /// </summary>
    public class ZoneState
    {
        public int ZoneId { get; set; }
        public string Name { get; set; } = string.Empty;

        // Config fields
        public double StartPoint { get; set; }
        public double EndPoint { get; set; }
        public bool Enabled { get; set; }
        public double AverageTemperature { get; set; }
        public double MaxTemperature { get; set; }
        public double MinTemperature { get; set; }
        public double MaxTempPosition { get; set; } // Position in meters where max temp was measured

        /// <summary>
        /// Rate of Rise in °C/minute. Calculated as temperature change per minute.
        /// Positive values indicate rising temperature, negative indicates cooling.
        /// </summary>
        public double RateOfRise { get; set; }

        /// <summary>
        /// Deviation in °C. Calculated as (MaxTemperature - MinTemperature) within the zone.
        /// </summary>
        public double Deviation { get; set; }
        public bool ActiveAlarm { get; set; }

        public bool PreAlarm { get; set; }
        public bool MaxAlarm { get; set; }
        public bool MinAlarm { get; set; }
        public bool RorAlarm { get; set; }
        public bool DeviationAlarm { get; set; }
        public DateTime LastUpdate { get; set; }

        public bool IsFirstTime { get; set; }

        // Alarm breach tracking - stores actual values and positions when alarms are triggered
        // Null when no alarm, contains breach data when alarm is active
        public double? PreAlarmBreachValue { get; set; }
        public double? PreAlarmBreachPosition { get; set; }

        public double? MaxAlarmBreachValue { get; set; }
        public double? MaxAlarmBreachPosition { get; set; }

        public double? MinAlarmBreachValue { get; set; }
        public double? MinAlarmBreachPosition { get; set; }

        public double? RorAlarmBreachValue { get; set; }
        public double? RorAlarmBreachPosition { get; set; }

        public double? DeviationAlarmBreachValue { get; set; }
        public double? DeviationAlarmBreachPosition { get; set; }
        public ZoneState(int zoneId, string name, double startPoint, double endPoint, double averageTemperature, double maxTemperature, double minTemperature, bool preAlarm, bool maxAlarm, bool minAlarm, DateTime lastUpdate)
        {
            ZoneId = zoneId;
            Name = name;
            StartPoint = startPoint;
            EndPoint = endPoint;
            AverageTemperature = averageTemperature;
            MaxTemperature = maxTemperature;
            MinTemperature = minTemperature;
            PreAlarm = preAlarm;
            MaxAlarm = maxAlarm;
            MinAlarm = minAlarm;
            LastUpdate = lastUpdate;
            IsFirstTime = true;
        }
    }
}
