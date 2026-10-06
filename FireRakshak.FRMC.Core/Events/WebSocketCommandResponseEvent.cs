using System.Text.Json;

namespace FireRakshak.FRMC.Core.Events
{
    /// <summary>
    /// Represents an event published when a WebSocket command has been processed and a response is ready.
    /// </summary>
    public class WebSocketCommandResponseEvent : IEvent
    {
        public string ConnectionId { get; }
        public string MessageType { get; }
        public JsonElement Payload { get; }

        public WebSocketCommandResponseEvent(string connectionId, string messageType, JsonElement payload)
        {
            ConnectionId = connectionId;
            MessageType = messageType;
            Payload = payload;
        }
    }
}
