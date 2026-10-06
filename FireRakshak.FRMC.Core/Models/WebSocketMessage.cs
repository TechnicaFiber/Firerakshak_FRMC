using System.Text.Json;

namespace FireRakshak.FRMC.Core.Models
{
    public class WebSocketMessage
    {
        public string MessageType { get; set; }
        public JsonElement Payload { get; set; }

        public WebSocketMessage() { }

        public WebSocketMessage(string messageType, JsonElement payload)
        {
            MessageType = messageType;
            Payload = payload;
        }
    }
}