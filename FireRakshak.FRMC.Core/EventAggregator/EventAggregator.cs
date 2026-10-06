using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FireRakshak.FRMC.Core.Events;
using Microsoft.Extensions.Logging;

namespace FireRakshak.FRMC.Core.EventAggregator
{
    /// <summary>
    /// Provides a thread-safe implementation of an event aggregator for publishing and subscribing to events.
    /// This class manages event subscriptions and dispatches published events to all registered handlers
    /// in an asynchronous and concurrent manner.
    /// FIXED: Added unsubscribe support to prevent memory leaks.
    /// </summary>
    public class EventAggregator : IEventAggregator
    {
        // FIXED: Changed from ConcurrentBag to Dictionary with subscription IDs for efficient removal
        private readonly ConcurrentDictionary<Type, ConcurrentDictionary<Guid, Func<IEvent, Task>>> _subscriptions =
            new ConcurrentDictionary<Type, ConcurrentDictionary<Guid, Func<IEvent, Task>>>();

        private readonly ILogger<EventAggregator> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="EventAggregator"/> class.
        /// </summary>
        /// <param name="logger">The logger for recording event publishing and handling activities, especially errors.</param>
        public EventAggregator(ILogger<EventAggregator> logger)
        {
            _logger = logger;
        }

        /// <inheritdoc/>
        public async Task PublishAsync<TEvent>(TEvent @event) where TEvent : IEvent
        {
            if (@event == null)
            {
                _logger.LogWarning("Attempted to publish a null event of type {EventType}.", typeof(TEvent).Name);
                return;
            }

            // Attempt to retrieve handlers for the specific event type.
            if (_subscriptions.TryGetValue(typeof(TEvent), out var handlers))
            {
                _logger.LogDebug("Publishing event {EventType} to {HandlerCount} handlers.", typeof(TEvent).Name, handlers.Count);

                // Execute all handlers concurrently using Task.WhenAll.
                // This ensures that event publishing does not block the caller, and handlers can run in parallel.
                try
                {
                    await Task.WhenAll(handlers.Values.Select(handler => handler(@event)));
                }
                catch (AggregateException ex)
                {
                    // Log exceptions from individual handlers.
                    // AggregateException is used because Task.WhenAll can throw multiple exceptions.
                    foreach (var innerEx in ex.InnerExceptions)
                    {
                        _logger.LogError(innerEx, "Error in event handler for event type {EventType}.", typeof(TEvent).Name);
                    }
                }
                catch (Exception ex)
                {
                    // Catch any other unexpected exceptions during handler execution.
                    _logger.LogError(ex, "An unexpected error occurred while publishing event {EventType}.", typeof(TEvent).Name);
                }
            }
            else
            {
                _logger.LogDebug("No handlers subscribed for event type {EventType}.", typeof(TEvent).Name);
            }
        }

        /// <inheritdoc/>
        /// FIXED: Now returns a subscription token that can be disposed to unsubscribe
        public IDisposable Subscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : IEvent
        {
            if (handler == null)
            {
                _logger.LogWarning("Attempted to subscribe a null handler for event type {EventType}.", typeof(TEvent).Name);
                return new NullSubscription();
            }

            var eventType = typeof(TEvent);
            var subscriptionId = Guid.NewGuid();

            // Get or add a dictionary for the event type
            var handlers = _subscriptions.GetOrAdd(eventType, _ => new ConcurrentDictionary<Guid, Func<IEvent, Task>>());

            // Add the new handler, wrapping it to accept IEvent and cast to TEvent
            handlers.TryAdd(subscriptionId, e => handler((TEvent)e));

            _logger.LogDebug("Handler subscribed for event type {EventType} with ID {SubscriptionId}.", eventType.Name, subscriptionId);

            // Return a subscription token that can be used to unsubscribe
            return new Subscription(this, eventType, subscriptionId, _logger);
        }

        /// <inheritdoc/>
        /// ADDED: Unsubscribe method to remove handler
        public void Unsubscribe(IDisposable subscription)
        {
            if (subscription is Subscription sub)
            {
                sub.Dispose();
            }
        }

        /// <summary>
        /// Internal method to remove a subscription by ID
        /// </summary>
        private void RemoveSubscription(Type eventType, Guid subscriptionId)
        {
            if (_subscriptions.TryGetValue(eventType, out var handlers))
            {
                if (handlers.TryRemove(subscriptionId, out _))
                {
                    _logger.LogDebug("Handler unsubscribed for event type {EventType} with ID {SubscriptionId}.",
                        eventType.Name, subscriptionId);

                    // If no more handlers, remove the event type entry
                    if (handlers.IsEmpty)
                    {
                        _subscriptions.TryRemove(eventType, out _);
                        _logger.LogDebug("No more handlers for event type {EventType}, removed from subscriptions.",
                            eventType.Name);
                    }
                }
            }
        }

        /// <summary>
        /// Subscription token class that allows unsubscription via Dispose pattern
        /// </summary>
        private class Subscription : IDisposable
        {
            private readonly EventAggregator _aggregator;
            private readonly Type _eventType;
            private readonly Guid _subscriptionId;
            private readonly ILogger _logger;
            private bool _disposed;

            public Subscription(EventAggregator aggregator, Type eventType, Guid subscriptionId, ILogger logger)
            {
                _aggregator = aggregator;
                _eventType = eventType;
                _subscriptionId = subscriptionId;
                _logger = logger;
            }

            public void Dispose()
            {
                if (_disposed)
                    return;

                _disposed = true;
                _aggregator.RemoveSubscription(_eventType, _subscriptionId);
            }
        }

        /// <summary>
        /// Null subscription for when handler is null
        /// </summary>
        private class NullSubscription : IDisposable
        {
            public void Dispose()
            {
                // Nothing to do
            }
        }
    }
}
