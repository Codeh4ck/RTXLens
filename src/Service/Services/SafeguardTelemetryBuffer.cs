using RTXLens.Contracts.Discovery;
using RTXLens.Contracts.Sensors;
using RTXLens.Plugins.Msi.Contracts.Safeguard;

namespace RTXLens.Service.Services;

internal sealed class SafeguardTelemetryBuffer(Dictionary<string, PluginHardwareMetadata>? sources = null)
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan MaximumCaptureAge = TimeSpan.FromSeconds(30);
    private readonly Lock _gate = new();
    private readonly LinkedList<SafeguardTelemetry> _frames = new();
    private SafeguardTelemetry? _latest;

    public SafeguardTelemetry WithSources(SafeguardTelemetry frame) => frame.Sources is null ? frame with { Sources = sources } : frame;

    public void Observe(SensorSnapshot snapshot)
    {
        lock (_gate)
        {
            var now = snapshot.Timestamp;
            if (_latest is not null && now < _latest.CapturedAt)
            {
                _frames.Clear();
            }

            _latest = new(now, snapshot.Sensors) { SettingsProfile = snapshot.SettingsProfile, Sources = sources };
            if (_frames.Last is not { } last || now - last.Value.CapturedAt >= SampleInterval)
            {
                _frames.AddLast(_latest);
            }

            // Keep one predecessor for a trigger between samples at the start of the window.
            while (_frames.First?.Next is { } next && now - next.Value.CapturedAt >= MaximumCaptureAge)
            {
                _frames.RemoveFirst();
            }
        }
    }

    public SafeguardTelemetry? CaptureAt(DateTimeOffset trigger)
    {
        lock (_gate)
        {
            var frame = _latest is not null && _latest.CapturedAt <= trigger ? _latest
                : _frames.LastOrDefault(candidate => candidate.CapturedAt <= trigger);
            return frame is null || trigger - frame.CapturedAt > MaximumCaptureAge ? null : frame with
            {
                Readings = [.. frame.Readings.Where(reading => reading.ObservedAt <= trigger)]
            };
        }
    }
}
