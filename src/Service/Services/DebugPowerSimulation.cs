using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Health;
using RTXLens.Contracts.Sensors;
using RTXLens.Contracts.Warnings;
using RTXLens.Core.Plugins;
using RTXLens.Plugins.Asus.Contracts;
using RTXLens.Plugins.Asus.Contracts.Warnings;
using RTXLens.Plugins.WireView.Contracts.Warnings;
using RTXLens.Warnings;

namespace RTXLens.Service.Services;

internal sealed class DebugPowerSimulation(HardwareCatalog hardware, TimeProvider clock)
{
    private sealed record Received(long Time, DebugPowerFrame Frame);
    private volatile Received? _latest;
    private readonly DeviceWarningEvaluator _evaluator = new(hardware.Find);
    private readonly Dictionary<string, ConfiguredWarning> _episodes = [];

    public void Receive(DebugPowerFrame frame)
    {
        if (frame.Pins is not { Length: <= 96 } || frame.Flags is not { Length: <= 24 }
            || frame.Pins.Any(p => p?.Identity is not { Channel: >= 1 and <= 6,
                Component: SensorComponent.GpuPin or SensorComponent.PsuPin,
                Metric: SensorMetric.Current or SensorMetric.Voltage } id || !ValidId(id.PluginId) || !ValidId(id.DeviceId))
            || frame.Flags.Any(f => f is null || !ValidId(f.PluginId) || !ValidId(f.DeviceId) || !ValidId(f.TypeId))
            || frame.History is { } history && !ValidHistory(history))
        {
            throw new ArgumentException("Invalid debug connector frame.");
        }

        _latest = new(clock.GetTimestamp(), frame);
    }

    // This payload is used only for history. Power reactions still use the independently evaluated pins and flags.
    public DebugWarningHistoryFrame? GetHistory() =>
        _latest is { } latest && clock.GetElapsedTime(latest.Time) < TimeSpan.FromSeconds(5) ? latest.Frame.History : null;

    private static bool ValidHistory(DebugWarningHistoryFrame history) =>
        history.Warnings is { Length: <= 224 } && history.Readings is { Length: <= 512 }
        && history.Warnings.All(warning => warning is { Title.Length: > 0 and <= 512, Detail.Length: <= 4096,
            Thresholds.Length: <= 16 } && ValidId(warning.PluginId) && ValidId(warning.DeviceId)
            && Enum.IsDefined(warning.Phase) && Guid.TryParseExact(warning.EpisodeId, "N", out _)
            && warning.Thresholds.All(threshold => threshold is { OffendingPins.Length: <= 6 }))
        && history.Readings.All(reading => reading?.Identity is { } identity && ValidId(identity.PluginId) && ValidId(identity.DeviceId));

    private static bool ValidId(string? id) => id is { Length: > 0 and <= 512 };

    public IReadOnlyList<ConfiguredWarning> Include(SensorSnapshot live, ServiceConfiguration configuration,
        DeviceWarningProfile[] profiles, SettingsProfile? automatic, IReadOnlyList<ConfiguredWarning> realWarnings)
    {
        var latest = _latest;
        if (latest is null)
        {
            return realWarnings;
        }

        var enabled = profiles.Where(p => p.Enabled && p.PowerReduction is not null
                                                    && configuration.Plugins.TryGetValue(p.PluginId, out var plugin) && plugin.Enabled).ToArray();
        if (enabled.Length == 0)
        {
            _episodes.Clear();
            return realWarnings;
        }

        var fresh = clock.GetElapsedTime(latest.Time) < TimeSpan.FromSeconds(5);
        var pins = new List<SensorReading>();
        if (fresh)
        {
            foreach (var pin in latest.Frame.Pins)
            {
                var id = pin.Identity;
                var device = Resolve(id.PluginId, id.DeviceId, id.ConnectorId, enabled);
                if (device is not null)
                {
                    pins.Add(pin with { Identity = id with { DeviceId = device } });
                }
            }
        }
        var debug = new SensorSnapshot(1, live.Sequence, live.Timestamp, [.. pins],
            [.. pins.Select(p => p.Identity.PluginId).Distinct().Select(id => new PluginHealth(id, PluginState.Ready, live.Timestamp, null))], []);
        var evaluated = _evaluator.Evaluate(debug, configuration, enabled, automatic);
        var observed = new HashSet<string>();
        foreach (var warning in evaluated.Warnings)
        {
            var observation = warning.Observation;
            if (!pins.Any(p => p.Identity.PluginId == observation.PluginId && p.Identity.DeviceId == observation.DeviceId
                && (p.Identity.ConnectorId is null || enabled.Any(profile => profile.Id == observation.ProfileId && profile.ConnectorId == p.Identity.ConnectorId))))
            {
                continue;
            }

            Remember(warning);
        }
        if (fresh)
        {
            foreach (var flag in latest.Frame.Flags.Where(f => f.TypeId is AsusWarningIds.Overcurrent or AsusWarningIds.Imbalance
                or WireViewWarningIds.PinOvercurrent or WireViewWarningIds.Imbalance))
            {
                var device = Resolve(flag.PluginId, flag.DeviceId, null, enabled);
                if (device is null)
                {
                    continue;
                }

                foreach (var profile in enabled.Where(p => p.PluginId == flag.PluginId && p.ConnectorId is null
                                                                                       && (p.DeviceId is null || p.DeviceId == device)))
                {
                    var observation = new WarningObservation(flag.PluginId, device, flag.TypeId, true, true, profile.Name) { ProfileId = profile.Id };
                    Remember(new(new(flag.TypeId, flag.PluginId, flag.TypeId.Split('/')[^1]), observation, profile.NotificationPolicy));
                }
            }
        }

        // Ending a simulation clears its observations through the ordinary recovery window.
        // Keep a clear observation available for the power worker, including manual holds.
        foreach (var (id, warning) in _episodes.ToArray())
        {
            if (!enabled.Any(p => p.Id == warning.Observation.ProfileId))
            {
                _episodes.Remove(id);
            }
            else if (!observed.Contains(id))
            {
                _episodes[id] = warning with { Observation = warning.Observation with { Available = true, Active = false } };
            }
        }
        return _episodes.Count == 0 ? realWarnings : [.. realWarnings, .. _episodes.Values];

        void Remember(ConfiguredWarning warning)
        {
            var typeId = "debug/" + warning.Observation.TypeId;
            // Device matching happened in the evaluator. Anchor the synthetic episode to
            // its saved target so a renamed virtual device cannot create orphaned holds.
            var profile = enabled.First(p => p.Id == warning.Observation.ProfileId);
            var observation = warning.Observation with { TypeId = typeId, DeviceId = profile.PowerReduction!.GpuId };
            if (!observation.Active && !_episodes.ContainsKey(observation.Id))
            {
                return;
            }

            var marked = warning with { Observation = observation,
                Definition = warning.Definition with { Id = typeId, Title = "Debug · " + warning.Definition.Title } };
            observed.Add(observation.Id);
            _episodes[observation.Id] = marked;
        }
    }

    private static string? Resolve(string plugin, string device, string? connector, DeviceWarningProfile[] profiles)
    {
        if (device != "debug-" + plugin)
        {
            return device;
        }

        // A virtual device has no physical identity. Only infer one when the saved
        // profiles for this source/connector refer to one physical GPU and device.
        var matches = profiles.Where(p => p.PluginId == plugin && p.ConnectorId == connector).ToArray();
        if (matches.Select(p => p.PowerReduction!.GpuId).Distinct().Count() != 1)
        {
            return null;
        }

        var devices = matches.Select(p => p.DeviceId ?? (plugin == AsusIdentity.Id ? p.PowerReduction!.GpuId : device)).Distinct().ToArray();
        return devices.Length == 1 ? devices[0] : null;
    }
}
