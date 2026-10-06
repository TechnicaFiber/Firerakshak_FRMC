using FireRakshak.FRMC.Core.Models;
using System.Collections.Generic;


namespace FireRakshak.FRMC.Core.Events
{
    public class ChannelsReceivedEvent : IEvent
    {
        public List<Channel> Channels { get; }
        public ChannelsReceivedEvent(List<Channel> channels)
        {
            Channels = channels;
        }
    }

    public class ZonesReceivedEvent : IEvent
    {
        public int ChannelId { get; }
        public List<Zone> Zones { get; }
        public ZonesReceivedEvent(int channelId, List<Zone> zones)
        {
            ChannelId = channelId;
            Zones = zones;
        }
    }

    public class ZoneConfigurationReceivedEvent : IEvent
    {
        public ZoneConfiguration ZoneConfiguration { get; }
        public ZoneConfigurationReceivedEvent(ZoneConfiguration zoneConfiguration)
        {
            ZoneConfiguration = zoneConfiguration;
        }
    }

    public class ZoneConfigurationUpdatedEvent : IEvent
    {
        public ZoneConfiguration ZoneConfiguration { get; }
        public ZoneConfigurationUpdatedEvent(ZoneConfiguration zoneConfiguration)
        {
            ZoneConfiguration = zoneConfiguration;
        }
    }

    public class ZoneClearedEvent : IEvent
    {
        public int ChannelId { get; }
        public ZoneClearedEvent(int channelId)
        {
            ChannelId = channelId;
        }
    }

    public class ZoneAddedEvent : IEvent
    {
        public ZoneConfiguration ZoneConfiguration { get; }
        public ZoneAddedEvent(ZoneConfiguration zoneConfiguration)
        {
            ZoneConfiguration = zoneConfiguration;
        }
    }

    public class ZoneRemovedEvent : IEvent
    {
        public int ChannelId { get; }
        public int ZoneId { get; }
        public ZoneRemovedEvent(int channelId, int zoneId)
        {
            ChannelId = channelId;
            ZoneId = zoneId;
        }
    }

    public class WebSocketAcknowledgementEvent : IEvent
    {
        public string OriginalMessageType { get; }
        public bool Success { get; }
        public string Message { get; }
        public WebSocketAcknowledgementEvent(string originalMessageType, bool success, string message)
        {
            OriginalMessageType = originalMessageType;
            Success = success;
            Message = message;
        }
    }

    public class WebSocketErrorEvent : IEvent
    {
        public string OriginalMessageType { get; }
        public string ErrorMessage { get; }
        public WebSocketErrorEvent(string originalMessageType, string errorMessage)
        {
            OriginalMessageType = originalMessageType;
            ErrorMessage = errorMessage;
        }
    }
}
