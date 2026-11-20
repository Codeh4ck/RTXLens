using RTXLens.Contracts.Enums;

namespace RTXLens.Contracts.Discovery;

public sealed record PluginInfo(string Id, string Name, bool Enabled, PluginState State,
    DateTimeOffset? LastSuccessAt, string? Message, PluginCapability[] Capabilities)
{
    public PluginHardwareMetadata? Hardware { get; init; }
    public SensorMetric[] ConnectorMetrics { get; init; } = [];
    public Warnings.WarningDefinition[] Warnings { get; init; } = [];
}
