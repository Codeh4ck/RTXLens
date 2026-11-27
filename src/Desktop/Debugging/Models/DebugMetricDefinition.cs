using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;

namespace RTXLens.Debugging.Models;

public sealed record DebugMetricDefinition(string Group, string Label, SensorComponent Component, SensorMetric Metric,
    double DefaultValue, int? Channel = null, string? ConnectorId = null)
{
    public SensorUnit Unit => SensorUnits.For(Metric);
    public bool IsBoolean => Unit == SensorUnit.Boolean;
    public bool IsFault => Metric is SensorMetric.FaultStatus or SensorMetric.FaultLog;
    public bool IsInteger => IsBoolean || IsFault;
    public double Minimum => Unit == SensorUnit.Celsius ? -273.15 : 0;
    public double Maximum => IsBoolean ? 1 : IsFault ? ushort.MaxValue : Unit == SensorUnit.Percent ? 100 : 1_000_000;
    public SensorIdentity Identity(string plugin, string device) => new(plugin, device, Component, Metric, Channel, ConnectorId);
    public bool Matches(SensorIdentity identity) => identity.Component == Component && identity.Metric == Metric
                                                                                    && identity.Channel == Channel && identity.ConnectorId == ConnectorId;
}
