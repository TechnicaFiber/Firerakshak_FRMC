using System;
using System.Collections.Generic;
using FireRakshak.FRMC.Core.Enums;

namespace FireRakshak.FRMC.Core.Events
{
    /// <summary>
    /// Event triggered when configuration settings are changed.
    /// </summary>
    public class ConfigurationChangedEvent : IEvent
    {
        public ConfigurationType ConfigType { get; set; }  // ✅ Renamed from Type
        public string ChangedBy { get; set; }
        public string Source { get; set; }                  // ✅ Added Source property
        public Dictionary<string, object> Changes { get; set; }

        public ConfigurationChangedEvent(ConfigurationType configType, string changedBy, string source, Dictionary<string, object> changes)
        {
            ConfigType = configType;
            ChangedBy = changedBy;
            Source = source;
            Changes = changes;
        }
    }
}
