public class ChannelConfiguration
{
    public int ChannelId { get; set; }

    public string Name { get; set; } = string.Empty;

    public double ChannelLength { get; set; }
    public double CorrectionLength { get; set; }
    public bool IsEnabled { get; set; }
    public int ScanPeriod { get; set; }
    public int NumberOfZones { get; set; }
    public string State { get; set; } = string.Empty;

    // OLD compatibility
    public double Length
    {
        get => ChannelLength;
        set => ChannelLength = value;
    }

    public int FrequencyOfScanningSeconds
    {
        get => ScanPeriod;
        set => ScanPeriod = value;
    }
}
