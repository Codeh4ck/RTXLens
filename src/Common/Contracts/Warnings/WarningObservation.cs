namespace RTXLens.Contracts.Warnings;

public sealed record WarningObservation(string PluginId, string DeviceId, string TypeId, bool Available, bool Active, string Detail)
{
    public string? ProfileId { get; init; }
    public string? ConnectorId { get; init; }
    public WarningThreshold? Threshold { get; init; }

    public string Id => PluginId + "/" + Uri.EscapeDataString(DeviceId) + "/" + TypeId + (ProfileId is null ? "" : "/" + Uri.EscapeDataString(ProfileId));
}
