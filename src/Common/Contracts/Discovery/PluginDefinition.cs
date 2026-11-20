namespace RTXLens.Contracts.Discovery;

public sealed record PluginDefinition(string Id, string Name, PluginCapability[] Capabilities)
{
    public PluginHardwareMetadata? Hardware { get; init; }
    public Enums.SensorMetric[] ConnectorMetrics { get; init; } = [];
    public Warnings.WarningDefinition[] Warnings { get; init; } = [];
}
