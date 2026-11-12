using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Monitoring;
using RTXLens.Contracts.Sensors;
using RTXLens.Core.Plugins;

namespace RTXLens.Core;

public sealed class ConnectorProfileResolver(IEnumerable<IConnectorSourceProvider> providers)
{
    private readonly IConnectorSourceProvider[] _providers = [.. providers];

    public ConnectorProfiles CreateDefaults()
    {
        var profiles = new List<ConnectorSourceProfile> { Create("automatic", "Automatic", _providers, true) };
        profiles.AddRange(_providers.Where(provider => provider.IsDedicatedProfile)
            .Select(provider => Create(provider.Id, provider.DedicatedProfileName, [provider], false)));

        return new ConnectorProfiles { Version = 1, Profiles = profiles };
    }

    private static ConnectorSourceProfile Create(string id, string name, IConnectorSourceProvider[] providers, bool fallback) => new()
    {
        Id = id,
        Name = name,
        Metrics = [.. new[] { SensorMetric.Power, SensorMetric.Current, SensorMetric.Voltage }.Select(metric => new ConnectorMetricSelection
        {
            Metric = metric,
            AllowFallback = fallback,
            PreferredPlugins = [.. providers.Where(provider => provider.Priority(metric) is not null)
                .OrderBy(provider => provider.Priority(metric)).Select(provider => provider.Id)]
        })]
    };

    public SensorSnapshot Resolve(SensorSnapshot snapshot, ServiceConfiguration configuration)
    {
        var profiles = configuration.Monitoring.ConnectorProfiles;
        var profile = profiles.Profiles.FirstOrDefault(profile => profile.Id == profiles.ActiveProfileId);

        if (profile is null)
        {
            return snapshot;
        }

        var sources = _providers
            .Where(provider => configuration.Plugins.TryGetValue(provider.Id, out var settings) && settings.Enabled)
            .ToDictionary(provider => provider.Id, provider => provider.ConnectorSources(snapshot,
                configuration.Plugins[provider.Id], profile.Connectors.GetValueOrDefault(provider.Id)), StringComparer.Ordinal);

        var readings = snapshot.Sensors.ToDictionary(reading => reading.Identity.Key, StringComparer.Ordinal);
        var results = new List<ResolvedSelection>();

        foreach (var metric in profile.Metrics)
        {
            var limit = metric.AllowFallback ? metric.PreferredPlugins.Count : Math.Min(1, metric.PreferredPlugins.Count);
            ResolvedSelection selected = new(ConnectorProfiles.SelectionName(metric.Metric), SelectionState.Unavailable, null);

            for (var index = 0; index < limit; index++)
            {
                var candidates = sources
                    .GetValueOrDefault(metric.PreferredPlugins[index])?
                    .Where(identity => identity.Metric == metric.Metric && identity.Component is SensorComponent.GpuConnector or SensorComponent.PsuConnector).ToArray() ?? [];

                if (candidates.Length != 1 || !readings.TryGetValue(candidates[0].Key, out var reading)
                    || reading is not { Quality: ReadingQuality.Valid, Value: { } value } || !double.IsFinite(value)
                    || snapshot.Timestamp - reading.ObservedAt > TimeSpan.FromMilliseconds(configuration.Plugins[reading.Identity.PluginId].StaleAfterMs)
                    || reading.ObservedAt > snapshot.Timestamp + TimeSpan.FromSeconds(1)
                    || !snapshot.Plugins.Any(plugin => plugin.PluginId == reading.Identity.PluginId && plugin.State == PluginState.Ready))
                {
                    continue;
                }

                selected = selected with { State = index == 0 ? SelectionState.Selected : SelectionState.Fallback, SensorKey = reading.Identity.Key };
                break;
            }

            results.Add(selected);
        }

        return snapshot with { Selections = [.. snapshot.Selections.Where(selection => !selection.Name.StartsWith("gpu-status/", StringComparison.Ordinal)), .. results] };
    }
}
