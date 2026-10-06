using System;
using System.Threading.Tasks;
using FireRakshak.FRMC.Core.Events;

namespace FireRakshak.FRMC.Core.EventAggregator
{

/// <summary>
/// Defines the contract for an event aggregator. An event aggregator facilitates
/// communication between loosely coupled components by allowing them to publish
/// and subscribe to events without direct references to each other. This promotes
/// a more modular and maintainable architecture.
/// FIXED: Added Unsubscribe method to prevent memory leaks.
/// </summary>
public interface IEventAggregator
{
    /// <summary>
    /// Publishes an event asynchronously to all registered handlers for the specified event type.
    /// </summary>
    /// <typeparam name="TEvent">The type of the event to publish. Must implement <see cref="IEvent"/>.</typeparam>
    /// <param name="event">The event instance to publish.</param>
    /// <returns>A task representing the asynchronous publish operation. Handlers are executed concurrently.</returns>
    Task PublishAsync<TEvent>(TEvent @event) where TEvent : IEvent;

    /// <summary>
    /// Subscribes a handler function to a specific event type.
    /// When an event of <typeparamref name="TEvent"/> is published, the provided handler will be invoked.
    /// FIXED: Now returns a subscription token that can be used to unsubscribe.
    /// </summary>
    /// <typeparam name="TEvent">The type of the event to subscribe to. Must implement <see cref="IEvent"/>.</typeparam>
    /// <param name="handler">The asynchronous handler function that will be executed when the event is published.</param>
    /// <returns>A subscription token that can be used to unsubscribe the handler.</returns>
    IDisposable Subscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : IEvent;

    /// <summary>
    /// ADDED: Unsubscribes a handler from a specific event type using the subscription token.
    /// </summary>
    /// <param name="subscription">The subscription token returned from Subscribe.</param>
    void Unsubscribe(IDisposable subscription);
}
}
