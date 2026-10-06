namespace FireRakshak.FRMC.Core.Events
{
    /// <summary>
    /// Marker interface for all events in the FireRakshak FRMC system.
    /// This interface serves to provide a common base type for all event messages,
    /// enabling the <see cref="EventAggregator.EventAggregator"/> to handle them generically.
    /// Events are simple data structures that carry information about something that has occurred.
    /// </summary>
    public interface IEvent
    {
    }
}
