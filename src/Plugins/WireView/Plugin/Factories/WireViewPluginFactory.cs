using RTXLens.Plugins.WireView.Contracts;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Plugins.WireView.Analysis.Warnings;
using RTXLens.Warnings;
using RTXLens.Contracts.Configuration;
using RTXLens.Core.Plugins;
using RTXLens.Plugins.WireView.Device;
using RTXLens.Plugins.WireView.Streaming;
using RTXLens.Contracts.Discovery;

namespace RTXLens.Plugins.WireView.Factories;

// ReSharper disable once ContextualLoggerProblem
public sealed class WireViewPluginFactory(TimeProvider clock, WireViewStream stream, ILogger<WireViewPlugin> logger) : ISensorPluginFactory, IConnectorSourceProvider
{
    public IWarningSource WarningSource { get; } = new WireViewWarningSource();
    public PluginHardwareMetadata Hardware => WireViewIdentity.Hardware;
    public string Id => WireViewPlugin.PluginId;
    public string Name => WireViewIdentity.PluginName;
    public PluginCapability[] Capabilities => [PluginCapability.Telemetry, PluginCapability.Configuration,
        PluginCapability.Restart, PluginCapability.DeviceConfigurationRead, PluginCapability.FaultClear];

    public ISensorPlugin Create(PluginSettings settings) =>
        new WireViewPlugin(settings, clock, new WireViewProReader(logger), stream, watchApplication: true);

    public bool IsDedicatedProfile => true;
    public string DedicatedProfileName => "WireView";
    public int? Priority(SensorMetric metric) => metric switch
    {
        SensorMetric.Power or SensorMetric.Current or SensorMetric.Voltage => 20,
        _ => null
    };

    public bool? HardwarePresent(PluginSettings settings) => Stm32PortFinder.FindMatchingComPorts().Count > 0;

    public SensorIdentity[] ConnectorSources(SensorSnapshot snapshot, PluginSettings settings, string? connectorId) =>
    [
        .. snapshot.Sensors.Where(reading => reading.Identity.PluginId == Id
                                             && reading.Identity is { Component: SensorComponent.GpuConnector, Channel: 1 })
            .Select(reading => reading.Identity)
    ];

}
