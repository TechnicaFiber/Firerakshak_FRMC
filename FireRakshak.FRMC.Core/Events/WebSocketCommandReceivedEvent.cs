using System.Text.Json;
using FireRakshak.FRMC.Core.Models; // Added for UserSession

namespace FireRakshak.FRMC.Core.Events
{
    /// <summary>
    /// Represents an event published when a WebSocket command is received.
    /// </summary>
    public class WebSocketCommandReceivedEvent : IEvent
    {
        public UserSession Session { get; }
        public string MessageType { get; }
        public JsonElement Payload { get; }

        public WebSocketCommandReceivedEvent(UserSession session, string messageType, JsonElement payload)
        {
            Session = session;
            MessageType = messageType;
            Payload = payload;
        }
    }
}
