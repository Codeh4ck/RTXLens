using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Discovery;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Contracts.Warnings;
using RTXLens.Debugging.Models;
using RTXLens.Debugging.ViewModels;
using RTXLens.Plugins.Asus.Contracts.Warnings;
using RTXLens.Presentation.Telemetry;
using RTXLens.Warnings;

namespace RTXLens.Debugging.Processing;

internal sealed class DebugProfileWarnings
{
    private readonly DebugPluginViewModel[] _plugins;
    private readonly DeviceWarningEvaluator _evaluator;

    public DebugProfileWarnings(DebugPluginViewModel[] plugins)
    {
        _plugins = plugins;
        _evaluator = new DeviceWarningEvaluator(FindSimulatedHardware);
    }

    public DeviceWarningProfileCatalog? Catalog { get; set; }

    public SensorSnapshot Apply(SensorSnapshot snapshot, ServiceConfiguration configuration, WarningEngine engine)
    {
        if (Catalog is not { } catalog)
        {
            return snapshot with { Warnings = engine.Evaluate(snapshot, configuration, snapshot.Timestamp) };
        }

        var profiles = catalog.Profiles.Where(profile => profile.Enabled).ToLookup(profile => profile.PluginId);
        var active = new List<DeviceWarningProfile>();

        foreach (var plugin in _plugins.Where(plugin => plugin.Mode != DebugSourceMode.Live))
        {
            foreach (var profile in profiles[plugin.Id].Where(profile => UsesSimulatedPins(plugin, profile.ConnectorId)))
            {
                var device = profile.DeviceId;
                if (device is not null && plugin.IsSimulated && plugin.DeviceId == "debug-" + plugin.Id)
                {
                    var targets = profiles[plugin.Id]
                        .Where(p => p.ConnectorId == profile.ConnectorId && p.DeviceId is not null)
                        .Select(p => p.DeviceId).Distinct(StringComparer.Ordinal)
                        .Take(2).ToArray();

                    if (targets.Length == 1)
                    {
                        device = plugin.DeviceId;
                    }
                }

                active.Add(profile with { DeviceId = device, ShutdownAfterSeconds = null });
            }
        }

        var evaluation = _evaluator.Evaluate(snapshot, configuration, [.. active], catalog.Defaults, catalog.AstralDefaults);
        return evaluation.Snapshot with
        {
            Warnings = engine.Evaluate(evaluation.Snapshot, configuration, snapshot.Timestamp, evaluation.Warnings, UsePluginWarning)
        };
    }

    private PluginHardwareMetadata? FindSimulatedHardware(string id)
    {
        var plugin = _plugins.FirstOrDefault(plugin => plugin.Id == id);
        if (plugin is null || plugin.Mode == DebugSourceMode.Live || (!plugin.IsSimulated && !plugin.Metrics.Any(IsPinOverride)))
        {
            return null;
        }

        var sources = Catalog?.Sources ?? LegacyHardwareCatalog.Sources;
        var hardware = sources.GetValueOrDefault(id);

        if (hardware?.PinProfiles is { UpdateDetectionFlags: true } support && (Overrides(plugin, SensorMetric.OvercurrentDetected) || Overrides(plugin, SensorMetric.ImbalanceDetected)))
        {
            return hardware with { PinProfiles = support with { UpdateDetectionFlags = false } };
        }

        return hardware;
    }

    private bool UsePluginWarning(WarningObservation observation)
    {
        var support = FindSimulatedHardware(observation.PluginId)?.PinProfiles;
        if (support?.ReplacePluginWarnings != true && support?.OvercurrentAlert != observation.TypeId)
        {
            return true;
        }

        var plugin = _plugins.First(plugin => plugin.Id == observation.PluginId);
        return observation.TypeId switch
        {
            AsusWarningIds.Overcurrent => Overrides(plugin, SensorMetric.OvercurrentDetected),
            AsusWarningIds.Imbalance => Overrides(plugin, SensorMetric.ImbalanceDetected),
            _ => false
        };
    }

    private static bool UsesSimulatedPins(DebugPluginViewModel plugin, string? connector) => plugin.IsSimulated
                                                                                             || plugin.Metrics.Any(metric => metric.Definition.ConnectorId == connector && IsPinOverride(metric));

    private static bool IsPinOverride(DebugMetricViewModel metric) => metric.Mode != DebugMetricMode.Live
        && metric.Definition.Component is SensorComponent.GpuPin or SensorComponent.PsuPin
        && metric.Definition.Metric is SensorMetric.Current or SensorMetric.Voltage;

    private static bool Overrides(DebugPluginViewModel plugin, SensorMetric kind) =>
        plugin.Metrics.Any(metric => metric.Definition.Metric == kind && metric.Mode != DebugMetricMode.Live);
}
