namespace RTXLens.Contracts.Monitoring;

public sealed class MonitoringOptions
{
    public const int DefaultLogRetentionDays = 7;
    public const int MaxLogRetentionDays = 90;
    public const int DefaultLogStorageMb = 10;
    public const int MaxLogStorageMb = 50;

    public int StreamIntervalMs { get; set; } = 1000;
    public int EventLogRetentionDays { get; set; } = DefaultLogRetentionDays;
    public int EventLogStorageMb { get; set; } = DefaultLogStorageMb;
    public ConnectorProfiles ConnectorProfiles { get; set; } = new();
    public List<SensorSelection> Selections { get; set; } = [];

    public bool IsValid() => StreamIntervalMs is >= 100 and <= 30000
                             && EventLogRetentionDays is >= 1 and <= MaxLogRetentionDays
                             && EventLogStorageMb is >= 1 and <= MaxLogStorageMb
                             && Selections is not null
                             && ConnectorProfiles is not null && ConnectorProfiles.IsValid()
                             && Selections.All(x => x is not null && x.IsValid())
                             && Selections.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() == Selections.Count;
}
