using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Sensors;
using RTXLens.Contracts.Warnings;
using RTXLens.Warnings.State;

namespace RTXLens.Warnings;

public sealed class WarningEngine
{
    private readonly WarningRegistry _registry;
    private readonly Dictionary<string, WarningDefinition> _definitions;
    private readonly Dictionary<string, WarningEntry> _entries = new(StringComparer.Ordinal);
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private WarningState[] _previous = [];
    private long _notificationSequence;
    private bool _enabled = true;

    public WarningEngine(WarningRegistry registry)
    {
        _registry = registry;
        _definitions = registry.Definitions.ToDictionary(definition => definition.Id, StringComparer.Ordinal);
    }

    public long Revision { get; private set; }

    public WarningSnapshot Evaluate(SensorSnapshot snapshot, ServiceConfiguration configuration, DateTimeOffset now,
        IReadOnlyList<ConfiguredWarning>? configuredWarnings = null, Func<WarningObservation, bool>? includePluginWarning = null)
    {
        var options = configuration.Warnings;
        if (!options.IsValid())
        {
            throw new ArgumentException("Invalid warning configuration.");
        }

        var observations = new Dictionary<string, WarningObservation>(StringComparer.Ordinal);
        foreach (var source in _registry.Sources)
        {
            if (!configuration.Plugins.TryGetValue(source.PluginId, out var settings) || !settings.Enabled)
            {
                continue;
            }

            foreach (var observation in source.Observe(snapshot, settings))
            {
                if (includePluginWarning?.Invoke(observation) == false)
                {
                    continue;
                }

                if (observation.PluginId != source.PluginId || !_definitions.TryGetValue(observation.TypeId, out var definition)
                    || definition.PluginId != source.PluginId || string.IsNullOrWhiteSpace(observation.DeviceId)
                    || !observations.TryAdd(observation.Id, observation))
                {
                    throw new InvalidOperationException("A plugin emitted an invalid or unregistered warning observation.");
                }
            }
        }

        var configured = configuredWarnings?.ToDictionary(warning => warning.Observation.Id, StringComparer.Ordinal)
            ?? new Dictionary<string, ConfiguredWarning>(StringComparer.Ordinal);

        foreach (var (id, warning) in configured)
        {
            observations.Add(id, warning.Observation);
        }

        var due = new List<WarningEntry>();
        foreach (var observation in observations.Values.Where(observation => observation is { Available: true, Active: true }))
        {
            if (!_entries.ContainsKey(observation.Id))
            {
                var definition = configured.GetValueOrDefault(observation.Id)?.Definition ?? _definitions[observation.TypeId];
                var policy = configured.GetValueOrDefault(observation.Id)?.Policy ?? options.PolicyFor(observation.TypeId);
                // Keep the readings and limits from the frame that started this episode.
                var thresholds = observations.Values.Where(item => item.PluginId == observation.PluginId
                    && item.DeviceId == observation.DeviceId && item.ProfileId == observation.ProfileId
                    && item.ConnectorId == observation.ConnectorId && item.Threshold is not null)
                    .Select(item => item.Threshold! with { Detected = item.Available ? item.Active : null }).ToArray();
                _entries.Add(observation.Id, new WarningEntry(observation, definition, policy, now, thresholds));
            }
        }

        foreach (var (id, entry) in _entries.ToArray())
        {

            if ((entry.Observation.ProfileId is not null && !configured.ContainsKey(id))
                || !configuration.Plugins.TryGetValue(entry.Observation.PluginId, out var plugin) || !plugin.Enabled
                || (!observations.Values.Any(observation => observation.PluginId == entry.Observation.PluginId && observation.DeviceId == entry.Observation.DeviceId)
                    && observations.Values.Any(observation => observation.PluginId == entry.Observation.PluginId)))
            {
                _entries.Remove(id);
                continue;
            }

            var policy = configured.GetValueOrDefault(id)?.Policy ?? options.PolicyFor(entry.Observation.TypeId);
            var notificationsEnabled = options.NotificationsEnabled;
            var notifyEnabled = (!entry.NotificationsEnabled && notificationsEnabled) || (!entry.Policy.NotifyOnStart && policy.NotifyOnStart);

            entry.Policy = policy;
            entry.NotificationsEnabled = notificationsEnabled;

            if (!observations.TryGetValue(id, out var current) || !current.Available)
            {
                entry.Phase = WarningPhase.Unavailable;
                entry.ClearSince = null;
                continue;
            }

            if (!current.Active)
            {
                entry.ClearSince ??= now;
                entry.Phase = WarningPhase.Recovering;

                if (now - entry.ClearSince >= TimeSpan.FromSeconds(policy.ClearAfterSeconds))
                {
                    _entries.Remove(id);
                }

                continue;
            }

            entry.Observation = current;
            entry.Phase = WarningPhase.Active;
            entry.ClearSince = null;

            if (notificationsEnabled && (((entry.NotificationSequence == 0 || notifyEnabled) && policy.NotifyOnStart)
                || (policy.RepeatWhileActive && now - (entry.LastNotificationAt ?? entry.StartedAt) >= TimeSpan.FromSeconds(policy.RepeatIntervalSeconds))))
            {
                due.Add(entry);
            }
        }

        if (due.Count > 0)
        {
            _notificationSequence++;
            foreach (var entry in due)
            {
                entry.NotificationSequence = _notificationSequence;
                entry.LastNotificationAt = now;
            }
        }

        var states = _entries.Values.OrderBy(entry => entry.Observation.Id, StringComparer.Ordinal).Select(entry => entry.Snapshot()).ToArray();
        if (_enabled != options.NotificationsEnabled || !_previous.SequenceEqual(states))
        {
            Revision++;
        }

        _previous = states;
        _enabled = options.NotificationsEnabled;

        return new WarningSnapshot(_instanceId, _notificationSequence, _enabled, states);
    }
}
