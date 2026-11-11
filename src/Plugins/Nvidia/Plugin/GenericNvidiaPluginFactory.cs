using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Discovery;
using RTXLens.Core.Plugins;
using RTXLens.Hardware.Nvidia;
using RTXLens.Plugins.GenericNvidia.Analysis.Warnings;
using RTXLens.Plugins.GenericNvidia.Configuration;
using RTXLens.Plugins.GenericNvidia.Contracts.Configuration;
using RTXLens.Plugins.GenericNvidia.Diagnostics;
using RTXLens.Plugins.GenericNvidia.Serialization;
using RTXLens.Warnings;

namespace RTXLens.Plugins.GenericNvidia;

public sealed class GenericNvidiaPluginFactory(NvidiaTelemetryReader reader, INvidiaDiagnosticsReader? diagnostics = null) : ISensorPluginFactory, IConnectorSourceProvider
{
    public string Id => GenericNvidiaPlugin.PluginId;
    public string Name => "Nvidia";
    public IWarningSource WarningSource { get; } = new GenericNvidiaWarningSource();
    public PluginCapability[] Capabilities => [PluginCapability.Telemetry, PluginCapability.Configuration, PluginCapability.Restart, PluginCapability.DeviceSelection];

    public PluginSettings CreateDefaultSettings() => GenericNvidiaSettings.WithOptions(new PluginSettings(), new GenericNvidiaOptions());

    public JsonElement ReadBootstrapOptions(IConfigurationSection section) => JsonSerializer.SerializeToElement(
        section.Get<GenericNvidiaOptions>(binding => binding.ErrorOnUnknownConfiguration = true) ?? new GenericNvidiaOptions(),
        GenericNvidiaJsonContext.Default.GenericNvidiaOptions);

    public ServiceConfiguration MigrateConfiguration(ServiceConfiguration configuration)
    {
        configuration = GenericNvidiaConfigurationMigration.Upgrade(configuration);
        if (configuration.Monitoring.ConnectorProfiles.Version == 0 && configuration.Plugins.TryGetValue(Id, out var settings))
        {
            configuration = configuration with
            {
                Plugins = new Dictionary<string, PluginSettings>(configuration.Plugins, StringComparer.Ordinal)
                {
                    [Id] = GenericNvidiaSettings.WithOptions(settings, GenericNvidiaSettings.GetOptions(settings) with { ElectricalSource = "Automatic" })
                }
            };
        }

        return configuration;
    }

    public void ValidateSettings(PluginSettings settings) => GenericNvidiaSettings.GetOptions(settings);

    public ISensorPlugin Create(PluginSettings settings) => new GenericNvidiaPlugin(settings, reader, diagnostics);

    public int? Priority(SensorMetric metric) => metric switch
    {
        SensorMetric.Power or SensorMetric.Current => 0,
        SensorMetric.Voltage => 100,
        _ => null
    };

    public SensorIdentity[] ConnectorSources(SensorSnapshot snapshot, PluginSettings settings, string? connectorId) =>
    [
        .. snapshot.Sensors
            .Where(reading => reading.Identity.PluginId == Id && reading.Identity is { Component: SensorComponent.GpuConnector, Channel: 1 })
            .Select(reading => reading.Identity)
    ];

}
