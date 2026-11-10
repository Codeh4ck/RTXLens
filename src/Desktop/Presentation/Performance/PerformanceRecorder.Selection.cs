using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;

namespace RTXLens.Presentation.Performance;

public sealed partial class PerformanceRecorder
{
    private HashSet<(string Device, string Connector)> _activeMsiConnectors = [];

    private void UpdateMsiConnectors(SensorSnapshot snapshot, DateTimeOffset now)
    {
        var settings = _settings.GetValueOrDefault("msi-psu");
        if (settings is not { Enabled: true }
            || !snapshot.Plugins.Any(plugin => plugin is { PluginId: "msi-psu", State: PluginState.Ready }))
        {
            _activeMsiConnectors.Clear();
            return;
        }

        var connectors = snapshot.Sensors.Where(reading => reading is
            {
                Identity: { PluginId: "msi-psu", ConnectorId: not null, Metric: SensorMetric.Current,
                    Component: SensorComponent.PsuPin or SensorComponent.PsuConnector },
                Quality: ReadingQuality.Valid, Value: { } value
            }
            && double.IsFinite(value) && value >= 0 && reading.ObservedAt <= now
            && now - reading.ObservedAt <= TimeSpan.FromMilliseconds(settings.StaleAfterMs))
            .GroupBy(reading => (Device: reading.Identity.DeviceId, Connector: reading.Identity.ConnectorId!))
            .ToArray();

        var active = new HashSet<(string Device, string Connector)>();
        foreach (var device in connectors.GroupBy(connector => connector.Key.Device))
        {
            var loaded = device.Where(connector => connector.Any(reading => reading.Value > 0)).ToArray();
            if (loaded.Length > 0)
            {
                active.UnionWith(loaded.Select(connector => connector.Key));
            }
            else
            {
                // All-zero readings cannot distinguish an idle cable from an unused one.
                active.UnionWith(device.Select(connector => connector.Key).Where(_activeMsiConnectors.Contains));
            }
        }

        _activeMsiConnectors = active;
    }

    private bool IsActiveConnector(PerformanceTrace trace) => IsActiveConnector(trace.Identity);

    private bool IsActiveConnector(SensorIdentity identity) => identity.PluginId != "msi-psu"
        || identity.ConnectorId is null
        || _activeMsiConnectors.Contains((identity.DeviceId, identity.ConnectorId));

    public void SelectMany(IEnumerable<string> keys, bool selected)
    {
        var changed = false;
        lock (_gate)
        {
            foreach (var key in keys.Distinct(StringComparer.Ordinal))
            {
                if (!_traces.TryGetValue(key, out var state) || state.Selected == selected)
                {
                    continue;
                }

                if (!selected)
                {
                    AddGap(state);
                }

                state.Selected = selected;
                changed = true;
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }
}
