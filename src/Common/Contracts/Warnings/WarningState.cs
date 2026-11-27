namespace RTXLens.Contracts.Warnings;

public sealed record WarningState(string Id, string PluginId, string DeviceId, string TypeId, string Title, string Detail,
    WarningPhase Phase, string EpisodeId, DateTimeOffset StartedAt, WarningPolicy Policy, long NotificationSequence,
    DateTimeOffset? LastNotificationAt)
{
    public string? ProfileId { get; init; }
    public string? ConnectorId { get; init; }
    // ReSharper disable once TypeWithSuspiciousEqualityIsUsedInRecord.Global
    public WarningThreshold[] Thresholds { get; init; } = [];
    public string Icon { get; init; } = "warning";
}
