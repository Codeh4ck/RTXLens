using System.Text.RegularExpressions;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Hardware.Nvidia;
using RTXLens.Core.Configuration;
using RTXLens.Core.Plugins;
using RTXLens.Contracts.Discovery;

namespace RTXLens.Service.Configuration;

internal sealed partial class SettingsProfileResolver(SettingsProfileStore profiles, ActiveSettingsProfileState state, HardwareCatalog hardware)
{
    private SettingsProfile? _automatic;

    public SettingsProfile? Automatic => Volatile.Read(ref _automatic);

    public SettingsProfileCatalog Catalog() => profiles.Catalog(Automatic);

    public ActiveSettingsProfile? Resolve(SensorSnapshot snapshot, ServiceConfiguration configuration, NvidiaDevice[] devices)
    {
        var automatic = ResolveAutomatic(snapshot, configuration, devices);
        Volatile.Write(ref _automatic, automatic?.Profile);

        var (_, selected) = profiles.Selection();
        var effective = automatic;

        if (automatic is not null && selected is not null)
        {
            effective = automatic with
            {
                Profile = automatic.Profile with
                {
                    Id = selected.Id,
                    Name = selected.Name,
                    GpuModel = selected.GpuModel,
                    CurrentCeiling = selected.CurrentCeiling,
                    PowerCeilingPerPin = selected.PowerCeilingPerPin,
                    PowerFallbackPerPin = selected.PowerFallbackPerPin,
                    VoltageMinimum = selected.VoltageMinimum,
                    VoltageMaximum = selected.VoltageMaximum
                }
            };
        }

        state.Current = effective;
        return effective;
    }

    private ActiveSettingsProfile? ResolveAutomatic(SensorSnapshot snapshot, ServiceConfiguration configuration, NvidiaDevice[] devices)
    {
        var connectorReadings = snapshot.Sensors
            .Where(reading =>
                reading is { Quality: ReadingQuality.Valid, Value: { } value } && double.IsFinite(value)
                && reading.Identity.Component is SensorComponent.GpuConnector or SensorComponent.GpuPin
                && reading.Identity.Metric is SensorMetric.Power or SensorMetric.Current or SensorMetric.Voltage
                && snapshot.Plugins.Any(plugin => plugin.PluginId == reading.Identity.PluginId && plugin.State == PluginState.Ready))
            .Select(reading => reading.Identity)
            .ToArray();

        var selectedGpuReadings = snapshot.Sensors
            .Where(reading => hardware.Find(reading.Identity.PluginId)?.Association == GpuAssociation.SelectedGpu && reading.Quality == ReadingQuality.Valid)
            .Select(reading => reading.Identity.DeviceId)
            .ToHashSet(StringComparer.Ordinal);

        var psuReporting = snapshot.Sensors.Any(reading =>
            reading is { Identity: { Component: SensorComponent.PsuPin, Metric: SensorMetric.Current }, Quality: ReadingQuality.Valid }
            && hardware.Find(reading.Identity.PluginId)?.Association == GpuAssociation.PowerSupply
            && snapshot.Plugins.Any(plugin => plugin.PluginId == reading.Identity.PluginId && plugin.State == PluginState.Ready));

        if (devices.Length == 0 || (connectorReadings.Length == 0 && selectedGpuReadings.Count == 0 && !psuReporting))
        {
            return null;
        }

        var selectedId = hardware.SelectedDevice(configuration);
        var gpu = selectedId is null ? devices.FirstOrDefault() : devices.FirstOrDefault(device => device.Id == selectedId || device.LegacyId == selectedId);

        if (gpu is null)
        {
            return null;
        }

        var model = NvidiaModelNameRegex().Match(gpu.Name);
        if (!model.Success)
        {
            return null;
        }

        var sourceProfile = configuration.Monitoring.ConnectorProfiles.Profiles.FirstOrDefault(profile => profile.Id == configuration.Monitoring.ConnectorProfiles.ActiveProfileId);

        var inlineMonitorAssigned = devices.Length == 1 || sourceProfile?.Metrics.Any(metric => metric.PreferredPlugins.FirstOrDefault() is { } id && hardware.Find(id)?.Association == GpuAssociation.InlineMonitor) == true;

        var inlineDeviceIds = connectorReadings
            .Where(identity => hardware.Find(identity.PluginId)?.Association == GpuAssociation.InlineMonitor)
            .Select(identity => identity.DeviceId)
            .Distinct()
            .Take(2)
            .ToArray();

        var inlineDevice = inlineMonitorAssigned && inlineDeviceIds.Length == 1 ? inlineDeviceIds[0] : null;

        if (inlineDevice is null && !psuReporting && !selectedGpuReadings.Contains(gpu.Id) && !connectorReadings.Any(identity => hardware.Find(identity.PluginId)?.Association is GpuAssociation.GpuDevice or GpuAssociation.SelectedGpu && identity.DeviceId == gpu.Id))
        {
            return null;
        }

        var defaults = model.Groups[1].Value == "5080" ? SettingsProfile.Rtx5080 : SettingsProfile.Rtx5090;

        return new(hardware.ApplyDefaults(defaults, configuration) with { Notifications = configuration.Warnings }, gpu.Id, inlineDevice);
    }

    [GeneratedRegex(@"\bRTX\s+(5080|5090)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NvidiaModelNameRegex();
}
