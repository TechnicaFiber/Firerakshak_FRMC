using System;
using FireRakshak.FRMC.Core.Enums;

namespace FireRakshak.FRMC.Core.Models
{
    /// <summary>
    /// Represents a record of a configuration change for auditing purposes.
    /// </summary>
    public record ConfigurationChange(
        DateTime Timestamp,
        string ChangedBy,
        string ConfigType, // Using string for flexibility, could be ConfigurationType enum
        string Key,
        object OldValue,
        object NewValue
    );
}
