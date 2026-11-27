using RTXLens.Contracts.Sensors;
using RTXLens.Contracts.Warnings;
using RTXLens.Presentation.Settings;

namespace RTXLens.Presentation.Notifications;

public sealed class WarningTransitionTracker
{
    private readonly Dictionary<string, long> _seen = new(StringComparer.Ordinal);
    private string? _instance;
    private bool _enabled = true;

    public WarningNotification[] Evaluate(SensorSnapshot snapshot, DateTimeOffset now, AppPreferences? preferences = null)
        => Evaluate(snapshot.Warnings, preferences?.WarningNotifications ?? true);

    public WarningNotification[] Evaluate(WarningSnapshot? feed, bool enabled)
    {
        var due = TakeDue(feed, enabled);
        if (due.Length == 0)
        {
            return [];
        }

        var title = due.Length == 1 ? due[0].Title : $"RTX Lens - {due.Length} warnings";
        var message = string.Join(Environment.NewLine, due.Take(3).Select(state => due.Length == 1 ? state.Detail : state.Title + ": " + state.Detail));
        if (due.Length > 3)
        {
            message += $"{Environment.NewLine}+{due.Length - 3} more warnings";
        }

        return [new WarningNotification(feed!.InstanceId + "/" + feed.Sequence, title, message) { Episodes = due }];
    }

    public WarningState[] TakeDue(WarningSnapshot? feed, bool enabled)
    {
        if (!enabled)
        {
            _enabled = false;
            return [];
        }
        if (feed is null || !feed.NotificationsEnabled)
        {
            return [];
        }
        if (!_enabled || _instance != feed.InstanceId)
        {
            _seen.Clear();
        }
        _enabled = true;
        _instance = feed.InstanceId;
        var keys = feed.States.Select(state => state.Id + "/" + state.EpisodeId).ToHashSet(StringComparer.Ordinal);
        foreach (var old in _seen.Keys.Where(key => !keys.Contains(key)).ToArray())
        {
            _seen.Remove(old);
        }
        var due = new List<WarningState>();
        foreach (var state in feed.States.Where(state => state is { Phase: WarningPhase.Active, NotificationSequence: > 0 }
                                                         && (state.Policy.NotifyOnStart || state.Policy.RepeatWhileActive)))
        {
            var key = state.Id + "/" + state.EpisodeId;
            if (state.NotificationSequence > _seen.GetValueOrDefault(key))
            {
                _seen[key] = state.NotificationSequence;
                due.Add(state);
            }
        }
        return [.. due];
    }
}
