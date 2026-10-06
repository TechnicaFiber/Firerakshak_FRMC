using FireRakshak.FRMC.Core.EventAggregator;
using FireRakshak.FRMC.Core.Events;

namespace FireRakshak.FRMC.IntegrationTests.Mocks;

/// <summary>
/// Mock Event Aggregator for integration testing.
/// Records all published events for test assertions and supports synchronous event handling.
/// </summary>
public class MockEventAggregator : IEventAggregator
{
    private readonly Dictionary<Type, List<Delegate>> _handlers = new();
    private readonly List<IEvent> _publishedEvents = new();
    private readonly object _lock = new();

    public IReadOnlyList<IEvent> PublishedEvents => _publishedEvents.AsReadOnly();

    public async Task PublishAsync<TEvent>(TEvent @event) where TEvent : IEvent
    {
        lock (_lock)
        {
            _publishedEvents.Add(@event);
        }

        if (_handlers.TryGetValue(typeof(TEvent), out var handlers))
        {
            foreach (var handler in handlers.ToList())
            {
                var func = (Func<TEvent, Task>)handler;
                await func(@event);
            }
        }
    }

    public IDisposable Subscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : IEvent
    {
        var eventType = typeof(TEvent);

        lock (_lock)
        {
            if (!_handlers.ContainsKey(eventType))
            {
                _handlers[eventType] = new List<Delegate>();
            }
            _handlers[eventType].Add(handler);
        }

        return new Subscription(() =>
        {
            lock (_lock)
            {
                if (_handlers.TryGetValue(eventType, out var list))
                {
                    list.Remove(handler);
                }
            }
        });
    }

    public void Unsubscribe(IDisposable subscription)
    {
        subscription?.Dispose();
    }

    // Test helper methods
    public List<TEvent> GetPublishedEvents<TEvent>() where TEvent : IEvent
    {
        lock (_lock)
        {
            return _publishedEvents.OfType<TEvent>().ToList();
        }
    }

    public TEvent? GetLastPublishedEvent<TEvent>() where TEvent : IEvent
    {
        lock (_lock)
        {
            return _publishedEvents.OfType<TEvent>().LastOrDefault();
        }
    }

    public int GetEventCount<TEvent>() where TEvent : IEvent
    {
        lock (_lock)
        {
            return _publishedEvents.OfType<TEvent>().Count();
        }
    }

    public bool HasEventOfType<TEvent>() where TEvent : IEvent
    {
        lock (_lock)
        {
            return _publishedEvents.OfType<TEvent>().Any();
        }
    }

    public void ClearEvents()
    {
        lock (_lock)
        {
            _publishedEvents.Clear();
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _publishedEvents.Clear();
            _handlers.Clear();
        }
    }

    private class Subscription : IDisposable
    {
        private readonly Action _unsubscribe;
        private bool _disposed;

        public Subscription(Action unsubscribe)
        {
            _unsubscribe = unsubscribe;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _unsubscribe();
                _disposed = true;
            }
        }
    }
}
