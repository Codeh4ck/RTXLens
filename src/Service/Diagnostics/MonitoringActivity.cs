using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Monitoring;
using RTXLens.Contracts.Sensors;
using RTXLens.Contracts.Warnings;
using RTXLens.Core.Diagnostics;
using RTXLens.Core.Plugins;

namespace RTXLens.Service.Diagnostics;

internal sealed class MonitoringActivity(PluginRuntimeManager runtime, ILogger<MonitoringActivity> logger)
{
    private readonly Dictionary<string, PluginState> _pluginStates = new(StringComparer.Ordinal);
    private readonly Dictionary<SensorMetric, ResolvedSelection> _selections = new();
    private readonly Dictionary<string, WarningState> _warnings = new(StringComparer.Ordinal);
    private string? _connectorProfile;
    private string? _gpu;
    private bool _resolutionFailed;

    public SensorSnapshot ResolveConnectorSources(SensorSnapshot snapshot, Contracts.Configuration.ServiceConfiguration configuration)
    {
        try
        {
            var resolved = runtime.ConnectorProfiles.Resolve(snapshot, configuration);
            if (_resolutionFailed)
            {
                logger.LogActivity(LogLevel.Information, "connector.resolution-recovered", "Connector sources",
                    "Connector source resolution recovered.");
                _resolutionFailed = false;
            }

            return resolved;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (!_resolutionFailed)
            {
                _resolutionFailed = true;
                logger.LogActivity(LogLevel.Error, "connector.resolution-failed", "Connector sources",
                    "The service could not resolve connector readings. Other monitoring continues.",
                    eventViewer: true, exception: error);
            }

            return snapshot;
        }
    }

    public void Observe(SensorSnapshot snapshot, ConnectorProfiles profiles)
    {
        foreach (var plugin in snapshot.Plugins)
        {
            if (plugin.State == PluginState.Stale && _pluginStates.GetValueOrDefault(plugin.PluginId) != PluginState.Stale)
            {
                logger.LogActivity(LogLevel.Warning, "plugin.readings-stale", Source(plugin.PluginId), "Device readings went stale.");
            }
            else if (plugin.State == PluginState.Ready && _pluginStates.GetValueOrDefault(plugin.PluginId) == PluginState.Stale)
            {
                logger.LogActivity(LogLevel.Information, "plugin.readings-fresh", Source(plugin.PluginId), "Device readings are current again.");
            }

            _pluginStates[plugin.PluginId] = plugin.State;
        }

        var gpu = snapshot.Sensors.FirstOrDefault(reading => reading is { Quality: ReadingQuality.Valid, Identity: { PluginId: "generic-nvidia", Component: SensorComponent.GpuCore } })?.Identity.DeviceId
            ?? snapshot.SettingsProfile?.GpuDeviceId;

        if (gpu is not null && _gpu != gpu)
        {
            var message = _gpu is null ? "GPU detected for monitoring." : "The GPU selected for monitoring changed.";
            logger.LogActivity(LogLevel.Information, "gpu.selected", Source("generic-nvidia"), message);
            _gpu = gpu;
        }

        if (_connectorProfile != profiles.ActiveProfileId)
        {
            _connectorProfile = profiles.ActiveProfileId;
            var name = profiles.Profiles.FirstOrDefault(profile => profile.Id == _connectorProfile)?.Name;
            logger.LogActivity(LogLevel.Information, "connector.profile-selected", "Connector sources",
                "Active connector source profile changed.", name);
        }

        ObserveSelections(snapshot);
        ObserveWarnings(snapshot.Warnings);
    }

    private void ObserveSelections(SensorSnapshot snapshot)
    {
        foreach (var metric in new[] { SensorMetric.Power, SensorMetric.Current, SensorMetric.Voltage })
        {
            var current = snapshot.Selections.FirstOrDefault(selection => selection.Name == ConnectorProfiles.SelectionName(metric));
            if (current is null || _selections.GetValueOrDefault(metric) == current)
            {
                continue;
            }

            var previous = _selections.GetValueOrDefault(metric);
            _selections[metric] = current;

            var name = metric.ToString().ToLowerInvariant();
            if (current.State == SelectionState.Unavailable)
            {
                if (previous is { State: not SelectionState.Unavailable })
                {
                    logger.LogActivity(LogLevel.Warning, "connector.source-unavailable", "Connector sources",
                        $"No valid source remains for connector {name}.");
                }

                continue;
            }

            var identity = snapshot.Sensors.FirstOrDefault(reading => reading.Identity.Key == current.SensorKey)?.Identity;
            var detail = identity is null ? null : Source(identity.PluginId);
            if (identity?.ConnectorId is "1" or "2")
            {
                detail += $" connector {identity.ConnectorId}";
            }

            if (current.State == SelectionState.Fallback)
            {
                logger.LogActivity(LogLevel.Warning, "connector.source-fallback", "Connector sources",
                    $"Using a fallback source for connector {name}.", detail);
            }
            else
            {
                logger.LogActivity(LogLevel.Information, "connector.source-selected", "Connector sources",
                    $"Connector {name} is reporting from the preferred source.", detail);
            }
        }
    }

    private void ObserveWarnings(WarningSnapshot? snapshot)
    {
        var states = snapshot?.States ?? [];
        foreach (var state in states)
        {
            if (!_warnings.TryGetValue(state.EpisodeId, out var previous) && state.Phase == WarningPhase.Active)
            {
                logger.LogActivity(LogLevel.Warning, "protection.warning-active", Source(state.PluginId),
                    $"{state.Title} warning activated.", state.Detail);
            }
            else if (previous is { Phase: not WarningPhase.Unavailable } && state.Phase == WarningPhase.Unavailable)
            {
                logger.LogActivity(LogLevel.Information, "protection.warning-unavailable", Source(state.PluginId),
                    $"{state.Title} warning cannot be evaluated with the current readings.");
            }

            _warnings[state.EpisodeId] = state;
        }

        if (_warnings.Count == states.Length)
        {
            return;
        }

        var active = states.Select(state => state.EpisodeId).ToHashSet(StringComparer.Ordinal);
        foreach (var episode in _warnings.Keys.Where(episode => !active.Contains(episode)).ToArray())
        {
            var previous = _warnings[episode];
            _warnings.Remove(episode);
            logger.LogActivity(LogLevel.Information, "protection.warning-ended", Source(previous.PluginId),
                $"{previous.Title} warning is no longer active.");
        }
    }

    private string Source(string pluginId) => runtime.Factories.FirstOrDefault(factory => factory.Id == pluginId)?.Name ?? pluginId;
}
