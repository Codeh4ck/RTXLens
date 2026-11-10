using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Discovery;
using RTXLens.Core.Plugins;
using RTXLens.Plugins.Msi.Configuration;
using RTXLens.Plugins.Msi.Contracts.Configuration;
using RTXLens.Plugins.Msi.Device;
using RTXLens.Plugins.Msi.Serialization;

namespace RTXLens.Plugins.Msi.Factories;

public sealed class MsiPluginFactory(TimeProvider clock, ILoggerFactory loggerFactory) : ISensorPluginFactory, IConnectorSourceProvider
{
    public string Id => MsiPlugin.PluginId;
    public string Name => "MSI MPG Ai1300TS / Ai1600TS";
    public PluginCapability[] Capabilities => [PluginCapability.Telemetry, PluginCapability.Configuration, PluginCapability.Restart, PluginCapability.DeviceSelection];
    public PluginSettings CreateDefaultSettings() => MsiSettings.WithOptions(new PluginSettings(), new MsiOptions());
    public void ValidateSettings(PluginSettings settings) => MsiSettings.GetOptions(settings);
    public ISensorPlugin Create(PluginSettings settings) => new MsiPlugin(settings, clock,
        new MsiPsuReader(loggerFactory.CreateLogger<MsiPsuReader>()), loggerFactory.CreateLogger<MsiPlugin>());
    public JsonElement ReadBootstrapOptions(IConfigurationSection section) => JsonSerializer.SerializeToElement(
        section.Get<MsiOptions>(binding => binding.ErrorOnUnknownConfiguration = true) ?? new MsiOptions(), MsiJsonContext.Default.MsiOptions);

    private readonly Mapping.MsiConnectorSelection _connector = new(MsiPlugin.PluginId);
    public int? Priority(SensorMetric metric) => metric == SensorMetric.Current ? 30 : null;

    public bool? HardwarePresent(PluginSettings settings)
    {
        using var discovery = new MsiPsuReader(loggerFactory.CreateLogger<MsiPsuReader>());
        var selected = MsiSettings.GetOptions(settings).DevicePath;

        return discovery.Devices.Any(device => selected is null || device.DeviceId == selected);
    }

    public SensorIdentity[] ConnectorSources(SensorSnapshot snapshot, PluginSettings settings, string? connectorId) => _connector.Select(snapshot, connectorId);

}