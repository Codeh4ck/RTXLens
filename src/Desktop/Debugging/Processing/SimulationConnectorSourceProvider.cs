using RTXLens.Plugins.GenericNvidia.Contracts;
using RTXLens.Plugins.Msi.Contracts;
using RTXLens.Plugins.WireView.Contracts;
using RTXLens.Plugins.Asus.Contracts;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Core.Plugins;
using RTXLens.Plugins.Msi.Mapping;

namespace RTXLens.Debugging.Processing;

internal sealed class SimulationConnectorSourceProvider(string id) : IConnectorSourceProvider
{
    private readonly MsiConnectorSelection _msi = new(id);
    public string Id => id;
    public bool IsDedicatedProfile => id == WireViewIdentity.Id;
    public string DedicatedProfileName => "WireView";
    public int? Priority(SensorMetric metric) => (id, metric) switch
    {
        (NvidiaIdentity.Id, SensorMetric.Power or SensorMetric.Current) => 0,
        (NvidiaIdentity.Id, SensorMetric.Voltage) => 100,
        (AsusIdentity.Id, SensorMetric.Power or SensorMetric.Current) => 10,
        (AsusIdentity.Id, SensorMetric.Voltage) => 0,
        (WireViewIdentity.Id, SensorMetric.Power or SensorMetric.Current or SensorMetric.Voltage) => 20,
        (MsiIdentity.Id, SensorMetric.Current) => 30,
        _ => null
    };

    public SensorIdentity[] ConnectorSources(SensorSnapshot snapshot, PluginSettings settings, string? connectorId) =>
        id == MsiIdentity.Id ? _msi.Select(snapshot, connectorId)
        : [.. snapshot.Sensors.Where(reading => reading.Identity.PluginId == id
                                                && reading.Identity is { Component: SensorComponent.GpuConnector, Channel: 1 }).Select(reading => reading.Identity)];
}
