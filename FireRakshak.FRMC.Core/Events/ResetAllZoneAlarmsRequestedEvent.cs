using FireRakshak.FRMC.Core.Events;
using System;

public class ResetAllZoneAlarmsRequestedEvent : IEvent
{
    public DateTime Timestamp { get; }

    public ResetAllZoneAlarmsRequestedEvent(DateTime timestamp)
    {
        Timestamp = timestamp;
    }
}
