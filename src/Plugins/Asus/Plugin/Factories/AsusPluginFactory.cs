using RTXLens.Plugins.Asus.Contracts;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Plugins.Asus.Analysis.Warnings;
using RTXLens.Warnings;
using RTXLens.Hardware.Nvidia;
using RTXLens.Plugins.Asus.Contracts.Configuration;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using RTXLens.Contracts.Configuration;
using RTXLens.Core.Plugins;
using RTXLens.Core.Configuration;
using RTXLens.Plugins.Asus.Configuration;
using RTXLens.Plugins.Asus.Device;
using RTXLens.Plugins.Asus.Streaming;
using RTXLens.Plugins.Asus.Serialization;
using RTXLens.Contracts.Discovery;

namespace RTXLens.Plugins.Asus.Factories;

public sealed class AsusPluginFactory(TimeProvider clock, AsusStream stream, NvidiaTelemetryReader reader, ActiveSettingsProfileState? profiles = null) : ISensorPluginFactory, IConnectorSourceProvider, IGpuProfileContributor
{
    public IWarningSource WarningSource { get; } = new AsusWarningSource();
    public PluginHardwareMetadata Hardware => AsusIdentity.Hardware;
    public string Id => AsusPlugin.PluginId;
    public string Name => AsusIdentity.PluginName;

    public PluginCapability[] Capabilities => [PluginCapability.Telemetry, PluginCapability.Configuration,
        PluginCapability.Restart, PluginCapability.DeviceSelection];

    public PluginSettings CreateDefaultSettings() => AsusSettings.WithOptions(new PluginSettings(),
        new AsusOptions { CoreTemperatureWarningC = 84, MemoryTemperatureWarningC = 95 });

    public JsonElement ReadBootstrapOptions(IConfigurationSection section) => JsonSerializer.SerializeToElement(
        section.Get<AsusOptions>(binding => binding.ErrorOnUnknownConfiguration = true) ?? new AsusOptions(), AsusJsonContext.Default.AsusOptions);

    public void ValidateSettings(PluginSettings settings) => AsusSettings.GetOptions(settings);
    public ISensorPlugin Create(PluginSettings settings) => new AsusPlugin(settings, clock, new AsusGpuReader(reader), stream, profiles);

    public SettingsProfile ApplyProfileDefaults(SettingsProfile profile, PluginSettings settings)
    {
        var options = AsusSettings.GetOptions(settings);
        var legacy = SettingsProfile.Rtx5090;
        return profile with
        {
            OvercurrentAmperes = Math.Abs(options.WireOvercurrentAmperageThreshold - (float)legacy.OvercurrentAmperes) < 0.001 ? profile.OvercurrentAmperes : options.WireOvercurrentAmperageThreshold,
            OvercurrentMilliseconds = options.WireOvercurrentThresholdMs,
            ImbalanceActivationAmperes = options.ImbalanceAmperageThreshold == legacy.ImbalanceActivationAmperes ? profile.ImbalanceActivationAmperes : options.ImbalanceAmperageThreshold,
            ImbalancePercentage = options.ImbalancePercentageThreshold == legacy.ImbalancePercentage ? profile.ImbalancePercentage : options.ImbalancePercentageThreshold,
            ImbalanceMilliseconds = options.ImbalanceDurationThresholdMs,
        };
    }

    public int? Priority(SensorMetric metric) => metric switch
    {
        SensorMetric.Power or SensorMetric.Current => 10,
        SensorMetric.Voltage => 0,
        _ => null
    };

    public bool? HardwarePresent(PluginSettings settings)
    {
        var selected = AsusSettings.GetOptions(settings).DeviceId;
        return reader.GetDevices().Where(device => selected is null || device.Id == selected || device.LegacyId == selected)
            .Any(device => device.IsAstral);
    }

    public SensorIdentity[] ConnectorSources(SensorSnapshot snapshot, PluginSettings settings, string? connectorId)
    {
        var gpus = snapshot.Sensors
            .Where(reading => reading.Identity.PluginId == Id)
            .Select(reading => reading.Identity.DeviceId)
            .ToHashSet();

        if (gpus.Count != 1 || snapshot.Sensors.Any(reading =>
                reading.Identity.PluginId == GenericNvidia.Contracts.NvidiaIdentity.Id && !gpus.Contains(reading.Identity.DeviceId)))
        {
            return [];
        }

        return
        [
            .. snapshot.Sensors
                .Where(reading => reading.Identity.PluginId == Id && reading.Identity.DeviceId == gpus.First() &&
                                  reading.Identity is { Component: SensorComponent.GpuConnector, Channel: 1 }).Select(reading => reading.Identity)
        ];
    }

}
