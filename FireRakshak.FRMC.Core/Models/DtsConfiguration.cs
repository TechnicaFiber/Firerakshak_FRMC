using System.Collections.Generic;

public class DtsConfiguration
{
    public byte PulseWidth { get; set; }
    public ushort PumpCurrent { get; set; }

    public List<byte> ChannelLengths { get; set; } = new();

    public DtsConfiguration() { }

    public DtsConfiguration(byte pulseWidth, ushort pumpCurrent, List<byte> channelLengths)
    {
        PulseWidth = pulseWidth;
        PumpCurrent = pumpCurrent;
        ChannelLengths = channelLengths;
    }
}
