using RTXLens.Contracts.Discovery;
using RTXLens.Contracts.Warnings;

namespace RTXLens.Contracts.Configuration;

public sealed record DeviceWarningProfile
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string PluginId { get; init; } = string.Empty;
    public string? DeviceId { get; init; }
    public string? ConnectorId { get; init; }
    public bool Enabled { get; init; } = true;
    public ConnectorWarningLimits? Limits { get; init; }
    public WarningPolicy? NotificationPolicy { get; init; }
    public int? ShutdownAfterSeconds { get; init; }

    public bool IsValid() => !string.IsNullOrWhiteSpace(Id) && Id.Length <= 80
        && !string.IsNullOrWhiteSpace(Name) && Name.Length <= 80
        && (DeviceId is null || !string.IsNullOrWhiteSpace(DeviceId) && DeviceId.Length <= 512)
        && !string.IsNullOrWhiteSpace(PluginId) && (ConnectorId is null || !string.IsNullOrWhiteSpace(ConnectorId))
        && (Limits is null || Limits.IsValid())
        && (NotificationPolicy is null || NotificationPolicy.IsValid())
        && ShutdownAfterSeconds is null or >= 1 and <= 86399;
}

public sealed record ConnectorWarningLimits(double OvercurrentAmperes, int OvercurrentMilliseconds,
    int ImbalanceActivationAmperes, int ImbalancePercentage, int ImbalanceMilliseconds,
    double LowPinVoltage, double HighPinVoltage)
{
    public static ConnectorWarningLimits From(SettingsProfile profile) => new(profile.OvercurrentAmperes,
        profile.OvercurrentMilliseconds, profile.ImbalanceActivationAmperes, profile.ImbalancePercentage,
        profile.ImbalanceMilliseconds, profile.LowPinVoltage, profile.HighPinVoltage);

    public bool IsValid() => OvercurrentAmperes is >= 6 and <= 10.5
        && OvercurrentMilliseconds is >= 100 and <= 30000
        && ImbalanceActivationAmperes is >= 1 and <= 10 && ImbalancePercentage is >= 10 and <= 50
        && ImbalanceMilliseconds is >= 100 and <= 30000
        && LowPinVoltage is >= 5 and < 12 && HighPinVoltage is > 12 and <= 20;
}

public sealed record WarningProfileTarget(string PluginId, string DeviceId, string Name);
public sealed record DeviceWarningProfileCatalog(DeviceWarningProfile[] Profiles, WarningProfileTarget[] Targets,
    ConnectorWarningLimits Defaults, ConnectorWarningLimits AstralDefaults, string GpuModel, WarningPolicy DefaultPolicy)
{
    public Dictionary<string, PluginHardwareMetadata>? Sources { get; init; }
}

public sealed record WarningShutdownReason(string Id, DateTimeOffset RequestedAt, DateTimeOffset BootTime,
    string Source, string ProfileName, string Warning, string Detail);
