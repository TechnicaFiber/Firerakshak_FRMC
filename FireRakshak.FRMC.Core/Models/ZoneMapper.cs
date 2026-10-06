using System;
using System.Collections.Generic;
using System.Linq;
using FireRakshak.FRMC.Core.Models;

namespace FireRakshak.FRMC.Core.Mappers
{
    public static class ZoneMapper
    {
        public static Dictionary<string, List<ZoneInfo>> ConvertToDtscmZoneMap(
            IEnumerable<Channel> channels)
        {
            var result = new Dictionary<string, List<ZoneInfo>>();

            foreach (var channel in channels)
            {
                if (channel == null || channel.Configuration == null)
                    continue;

                int channelId = channel.Configuration.ChannelId;
                string key = $"Channel {channelId}";

                var zones = new List<ZoneInfo>();

                if (channel.Zones != null)
                {
                    foreach (var z in channel.Zones)
                    {
                        if (z.Configuration == null)
                            continue;

                        zones.Add(new ZoneInfo
                        {
                            ZoneId = z.Configuration.ZoneId,
                            Name = z.Configuration.Name,
                            StartPoint = z.Configuration.StartPoint,
                            EndPoint = z.Configuration.EndPoint,
                            MaxTemp = z.Configuration.MaxTemp,
                            MinTemp = z.Configuration.MinTemp,
                            PreAlarm = z.Configuration.PreAlarm,
                            RoRThreshold = z.Configuration.RoRThreshold,
                            DeviationThreshold = z.Configuration.DeviationThreshold,
                            Enabled = z.Configuration.Enabled,

                            IsMaxTempEnabled = z.Configuration.IsMaxTempEnabled,
                            IsMinTempEnabled = z.Configuration.IsMinTempEnabled,
                            IsPreAlarmEnabled = z.Configuration.IsPreAlarmEnabled,
                            IsRateOfRiseEnabled = z.Configuration.IsRateOfRiseEnabled,
                            IsDeviationEnabled = z.Configuration.IsDeviationEnabled,
                            AssignedRelayId = z.Configuration.AssignedRelayId
                        });
                    }
                }

                result[key] = zones;
            }

            return result;
        }

        public static List<ZoneConfiguration> ConvertToFrmcZoneList(
            Dictionary<string, List<ZoneInfo>> zoneMap)
        {
            var result = new List<ZoneConfiguration>();

            foreach (var kvp in zoneMap)
            {
                int channelId = ParseChannelKey(kvp.Key);

                foreach (var zi in kvp.Value)
                {
                    result.Add(new ZoneConfiguration
                    {
                        ChannelId = channelId,
                        ZoneId = zi.ZoneId,
                        Name = zi.Name,
                        StartPoint = zi.StartPoint,
                        EndPoint = zi.EndPoint,
                        MaxTemp = zi.MaxTemp,
                        MinTemp = zi.MinTemp,
                        PreAlarm = zi.PreAlarm,
                        RoRThreshold = zi.RoRThreshold,
                        DeviationThreshold = zi.DeviationThreshold,
                        Enabled = zi.Enabled,

                        IsMaxTempEnabled = zi.IsMaxTempEnabled,
                        IsMinTempEnabled = zi.IsMinTempEnabled,
                        IsPreAlarmEnabled = zi.IsPreAlarmEnabled,
                        IsRateOfRiseEnabled = zi.IsRateOfRiseEnabled,
                        IsDeviationEnabled = zi.IsDeviationEnabled,
                        AssignedRelayId = zi.AssignedRelayId
                    });
                }
            }

            return result;
        }



        private static int ExtractZoneNumber(string zoneId)
        {
            if (string.IsNullOrWhiteSpace(zoneId))
                return 1;

            var parts = zoneId.Split('.');

            if (parts.Length == 2 && int.TryParse(parts[1], out int id))
                return id;

            return 1;
        }

        
        private static int ParseChannelKey(string key)
        {
            key = key.Trim();

            if (key.StartsWith("Channel ", StringComparison.OrdinalIgnoreCase))
                key = key.Substring("Channel ".Length);

            return int.TryParse(key, out int id) ? id : 1;
        }
    }
}
