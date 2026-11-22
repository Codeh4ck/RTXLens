using RTXLens.Contracts.Monitoring;

namespace RTXLens.Contracts.Configuration;

public sealed record ServiceConfiguration
{
    public Warnings.WarningOptions Warnings { get; init; } = new();
    public WidgetConfiguration Widget { get; init; } = new();
    public MonitoringOptions Monitoring { get; init; } = new();
    public Dictionary<string, PluginSettings> Plugins { get; init; } = new(StringComparer.Ordinal);
}
