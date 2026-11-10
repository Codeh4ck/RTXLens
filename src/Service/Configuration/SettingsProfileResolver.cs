using System.Text.RegularExpressions;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Hardware.Nvidia;
using RTXLens.Core.Configuration;
using RTXLens.Core.Diagnostics;
using RTXLens.Plugins.Asus.Configuration;
using RTXLens.Plugins.GenericNvidia.Configuration;

namespace RTXLens.Service.Configuration;

internal sealed partial class SettingsProfileResolver(SettingsProfileStore profiles, ActiveSettingsProfileState state,
    IServiceProvider services, ILogger<SettingsProfileResolver> logger)
{
    private DateTimeOffset _lastDiscoveryFailure;
    private bool _discoveryFailed;
    private SettingsProfile? _automatic;

    public SettingsProfile? Automatic => Volatile.Read(ref _automatic);

    public SettingsProfileCatalog Catalog() => profiles.Catalog(Automatic);

    public ActiveSettingsProfile? Resolve(SensorSnapshot snapshot, ServiceConfiguration configuration)
    {
        var automatic = ResolveAutomatic(snapshot, configuration);
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

    private ActiveSettingsProfile? ResolveAutomatic(SensorSnapshot snapshot, ServiceConfiguration configuration)
    {
        var connectorReadings = snapshot.Sensors
            .Where(reading =>
                reading is { Quality: ReadingQuality.Valid, Value: { } value } && double.IsFinite(value)
                && reading.Identity.Component is SensorComponent.GpuConnector or SensorComponent.GpuPin
                && reading.Identity.Metric is SensorMetric.Power or SensorMetric.Current or SensorMetric.Voltage
                && snapshot.Plugins.Any(plugin => plugin.PluginId == reading.Identity.PluginId && plugin.State == PluginState.Ready))
            .Select(reading => reading.Identity)
            .ToArray();

        var nvidiaReadings = snapshot.Sensors.Where(reading => reading.Identity.PluginId == "generic-nvidia"
            && reading.Quality == ReadingQuality.Valid).Select(reading => reading.Identity.DeviceId).ToHashSet(StringComparer.Ordinal);

        var msiReporting = snapshot.Plugins.Any(plugin => plugin is { PluginId: "msi-psu", State: PluginState.Ready })
            && snapshot.Sensors.Any(reading => reading is { Identity: { PluginId: "msi-psu", Component: SensorComponent.PsuPin, Metric: SensorMetric.Current }, Quality: ReadingQuality.Valid });

        if ((connectorReadings.Length == 0 && nvidiaReadings.Count == 0 && !msiReporting) || snapshot.Timestamp - _lastDiscoveryFailure < TimeSpan.FromSeconds(5))
        {
            return null;
        }

        NvidiaDevice[] devices;
        try
        {
            devices = services.GetService<NvidiaTelemetryReader>()?.GetDevices() ?? [];

            if (_discoveryFailed)
            {
                _discoveryFailed = false;
                logger.LogActivity(LogLevel.Information, "gpu.profile-detection-recovered", "NVIDIA",
                    "GPU detection for chart profiles recovered.");
            }
        }
        catch (Exception error)
        {
            _lastDiscoveryFailure = snapshot.Timestamp;

            if (!_discoveryFailed)
            {
                _discoveryFailed = true;
                logger.LogActivity(LogLevel.Warning, "gpu.profile-detection-failed", "NVIDIA",
                    "Could not identify the GPU for chart profiles. The service will retry.", eventViewer: true, exception: error);
            }
            return null;
        }

        var nvidia = configuration.Plugins.GetValueOrDefault("generic-nvidia");
        var nvidiaOptions = nvidia is null ? null : GenericNvidiaSettings.GetOptions(nvidia);
        var selectedId = nvidiaOptions?.DeviceId;
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

        var wireViewAssigned = devices.Length == 1 || sourceProfile?.Metrics.Any(metric => metric.PreferredPlugins.FirstOrDefault() == "wireview-pro2") == true;
        var wireViewIds = connectorReadings.Where(identity => identity.PluginId == "wireview-pro2").Select(identity => identity.DeviceId).Distinct().Take(2).ToArray();
        var wireView = wireViewAssigned && wireViewIds.Length == 1 ? wireViewIds[0] : null;

        if (wireView is null && !msiReporting && !nvidiaReadings.Contains(gpu.Id) && !connectorReadings.Any(identity =>
                identity.PluginId is "asus-astral" or "generic-nvidia" && identity.DeviceId == gpu.Id))
        {
            return null;
        }

        var defaults = model.Groups[1].Value == "5080" ? SettingsProfile.Rtx5080 : SettingsProfile.Rtx5090;

        // Preserve explicitly customized legacy settings in Auto. Old stock values inherit the model defaults.
        var asus = configuration.Plugins.GetValueOrDefault("asus-astral");
        var options = asus is null ? new RTXLens.Plugins.Asus.Contracts.Configuration.AsusOptions() : AsusSettings.GetOptions(asus);
        var legacy = SettingsProfile.Rtx5090;

        return new(defaults with
        {
            OvercurrentAmperes = Math.Abs(options.WireOvercurrentAmperageThreshold - (float)legacy.OvercurrentAmperes) < 0.001 ? defaults.OvercurrentAmperes : options.WireOvercurrentAmperageThreshold,
            OvercurrentMilliseconds = options.WireOvercurrentThresholdMs,
            ImbalanceActivationAmperes = options.ImbalanceAmperageThreshold == legacy.ImbalanceActivationAmperes ? defaults.ImbalanceActivationAmperes : options.ImbalanceAmperageThreshold,
            ImbalancePercentage = options.ImbalancePercentageThreshold == legacy.ImbalancePercentage ? defaults.ImbalancePercentage : options.ImbalancePercentageThreshold,
            ImbalanceMilliseconds = options.ImbalanceDurationThresholdMs,
            CoreTemperature = nvidiaOptions is null ? defaults.CoreTemperature : nvidiaOptions.CoreTemperatureWarningC,
            MemoryTemperature = nvidiaOptions is null ? defaults.MemoryTemperature : nvidiaOptions.MemoryTemperatureWarningC,
            Notifications = configuration.Warnings
        }, gpu.Id, wireView);
    }

    [GeneratedRegex(@"\bRTX\s+(5080|5090)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NvidiaModelNameRegex();
}
