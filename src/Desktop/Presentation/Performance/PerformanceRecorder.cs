using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Discovery;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;

namespace RTXLens.Presentation.Performance;

public sealed partial class PerformanceRecorder(TimeProvider? timeProvider = null, int maximumSamples = 2_000_000)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, TraceState> _traces = new(StringComparer.Ordinal);
    private Dictionary<string, PluginSettings> _settings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sourceNames = new(StringComparer.Ordinal)
    {
        ["asus-astral"] = "ROG Astral",
        ["wireview-pro2"] = "WireView Pro II",
        ["msi-psu"] = "MSI PSU",
        ["generic-nvidia"] = "NVIDIA"
    };
    private string _sessionId = Guid.NewGuid().ToString("N");
    private DateTimeOffset? _startedAt;
    private long _startedTimestamp;
    private double _duration;
    private bool _recording;
    private long _sampleCount;
    private string _status = "Choose metrics and sources to record.";
    private string _mode = "live";

    public event Action? Changed;

    public void SetConfiguration(ServiceConfiguration configuration)
    {
        lock (_gate)
        {
            _settings = new(configuration.Plugins, StringComparer.Ordinal);
            foreach (var state in _traces.Values.Where(state => _settings.GetValueOrDefault(state.Trace.Identity.PluginId)?.Enabled != true))
            {
                state.Available = false;
                AddGap(state);
            }
        }

        Changed?.Invoke();
    }

    public void SetCatalog(PluginCatalog catalog)
    {
        lock (_gate)
        {
            foreach (var plugin in catalog.Plugins)
            {
                _sourceNames.TryAdd(plugin.Id, plugin.Name);
            }
        }
    }

    public void SwitchMode(string mode)
    {
        lock (_gate)
        {
            if (_mode == mode)
            {
                return;
            }

            if (_recording)
            {
                StopCore("Recording stopped because the telemetry mode changed.");
            }

            MarkUnavailable();
            _mode = mode;
        }

        Changed?.Invoke();
    }

    public void Accept(SensorSnapshot snapshot)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            UpdateMsiConnectors(snapshot, now);
            var ready = snapshot.Plugins.Where(plugin => plugin.State == PluginState.Ready)
                .Select(plugin => plugin.PluginId).ToHashSet(StringComparer.Ordinal);
            var found = new HashSet<string>(StringComparer.Ordinal);
            UpdateDuration();

            foreach (var reading in snapshot.Sensors)
            {
                var identity = reading.Identity;
                var settings = _settings.GetValueOrDefault(identity.PluginId);
                var usable = settings is { Enabled: true } && ready.Contains(identity.PluginId)
                    && reading is { Quality: ReadingQuality.Valid, Value: { } value } && double.IsFinite(value)
                    && reading.ObservedAt <= now && now - reading.ObservedAt <= TimeSpan.FromMilliseconds(settings.StaleAfterMs);

                if (!_traces.TryGetValue(identity.Key, out var state))
                {
                    if (!usable || !IsActiveConnector(identity) || _traces.Count >= 256)
                    {
                        continue;
                    }

                    var trace = PerformanceTrace.Create(reading, _sourceNames.GetValueOrDefault(identity.PluginId, identity.PluginId), _traces.Count);
                    if (trace is null)
                    {
                        continue;
                    }

                    state = new TraceState(trace);
                    _traces.Add(trace.Key, state);
                }

                usable &= IsActiveConnector(identity) || state.Samples.Count > 0;
                found.Add(identity.Key);
                state.Available = usable;
                state.LastSeen = reading.ObservedAt;
                state.StaleAfter = TimeSpan.FromMilliseconds(settings?.StaleAfterMs ?? 5000);
                if (!usable)
                {
                    AddGap(state);
                    continue;
                }

                if (!_recording || !state.Selected || reading.ObservedAt < _startedAt
                    || state.LastRecorded is { } last && reading.ObservedAt <= last)
                {
                    continue;
                }

                var seconds = (reading.ObservedAt - _startedAt!.Value).TotalSeconds;
                if (state.LastRecorded is { } previous && reading.ObservedAt - previous > state.StaleAfter)
                {
                    AddGap(state);
                }

                if (_sampleCount >= maximumSamples)
                {
                    StopCore("Recording stopped at the session storage limit. Export this session before starting another.");
                    break;
                }

                var number = state.Trace.ConvertValue(reading.Value!.Value);
                state.Samples.Add(new(seconds, number));
                state.LastRecorded = reading.ObservedAt;
                state.HasOpenLine = true;
                _sampleCount++;
            }

            foreach (var state in _traces.Values.Where(state => !found.Contains(state.Trace.Key)))
            {
                state.Available = false;
                AddGap(state);
            }
        }

        Changed?.Invoke();
    }

    public void MarkDisconnected()
    {
        lock (_gate)
        {
            MarkUnavailable();
        }

        Changed?.Invoke();
    }

    public void Select(string key, bool selected)
    {
        lock (_gate)
        {
            if (!_traces.TryGetValue(key, out var state) || state.Selected == selected)
            {
                return;
            }

            if (!selected)
            {
                AddGap(state);
            }

            state.Selected = selected;
        }

        Changed?.Invoke();
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_recording)
            {
                return;
            }

            RefreshAvailability();
            if (!_traces.Values.Any(state => state is { Selected: true, Available: true } && IsActiveConnector(state.Trace)))
            {
                throw new InvalidOperationException("Select at least one available metric before recording.");
            }

            _sessionId = Guid.NewGuid().ToString("N");
            _startedAt = _clock.GetUtcNow();
            _startedTimestamp = _clock.GetTimestamp();
            _duration = 0;
            _sampleCount = 0;
            foreach (var state in _traces.Values)
            {
                state.Samples.Clear();
                state.DisplayCache = null;
                state.Imported = false;
                state.Available &= IsActiveConnector(state.Trace);
                state.LastRecorded = null;
                state.HasOpenLine = false;
            }

            _recording = true;
            _status = _mode == "live" ? "Recording" : "Recording simulated telemetry";
        }

        Changed?.Invoke();
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_recording)
            {
                StopCore("Recording complete.");
            }
        }

        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_recording)
            {
                throw new InvalidOperationException("Stop recording before clearing the session.");
            }

            RefreshAvailability();
            foreach (var key in _traces.Where(pair => pair.Value.Imported && !pair.Value.Available)
                .Select(pair => pair.Key).ToArray())
            {
                _traces.Remove(key);
            }

            foreach (var state in _traces.Values)
            {
                state.Samples.Clear();
                state.DisplayCache = null;
                state.Imported = false;
                state.Available &= IsActiveConnector(state.Trace);
                state.LastRecorded = null;
                state.HasOpenLine = false;
            }

            _sessionId = Guid.NewGuid().ToString("N");
            _startedAt = null;
            _duration = 0;
            _sampleCount = 0;
            _status = "Choose metrics and sources to record.";
        }

        Changed?.Invoke();
    }

    public PerformanceChartFrame ReadFrame(string? previousSessionId, IDictionary<string, int> cursors, bool includeSamples = true,
        bool includeDisabled = false)
    {
        lock (_gate)
        {
            RefreshAvailability();
            UpdateDuration();

            if (previousSessionId != _sessionId)
            {
                cursors.Clear();
            }

            var visible = _traces.Values.Where(state => includeDisabled || state.Imported
                || _settings.GetValueOrDefault(state.Trace.Identity.PluginId)?.Enabled == true
                    && (state.Samples.Count > 0 || IsActiveConnector(state.Trace))).ToArray();
            var visibleKeys = visible.Select(state => state.Trace.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var key in cursors.Keys.Where(key => !visibleKeys.Contains(key)).ToArray())
            {
                cursors.Remove(key);
            }

            var series = visible.Select(state =>
            {
                var offset = Math.Clamp(cursors.TryGetValue(state.Trace.Key, out var recorded) ? recorded : 0, 0, state.Samples.Count);
                var samples = includeSamples ? state.Samples.GetRange(offset, state.Samples.Count - offset).ToArray() : [];
                if (includeSamples)
                {
                    cursors[state.Trace.Key] = state.Samples.Count;
                }
                return new PerformanceSeries(state.Trace, state.Selected, state.Available, offset, state.Samples.Count, samples);
            }).ToArray();

            return new(_sessionId, _startedAt, _duration, _recording, _sampleCount, _status, series);
        }
    }

    public PerformanceChartFrame CompletedSession()
    {
        lock (_gate)
        {
            if (_recording || _sampleCount == 0)
            {
                throw new InvalidOperationException("Stop a recording with captured samples before exporting.");
            }

            var frame = ReadFrame(null, new Dictionary<string, int>(), includeDisabled: true);
            return frame with { Series = [.. frame.Series.Where(series => series.Count > 0)] };
        }
    }

    public void Import(PerformanceChartFrame session)
    {
        PerformanceRecordingFile.Validate(session);
        lock (_gate)
        {
            if (_recording)
            {
                throw new InvalidOperationException("Stop the current recording before importing another session.");
            }

            RefreshAvailability();
            var existing = new Dictionary<string, TraceState>(_traces, StringComparer.Ordinal);
            var imported = new Dictionary<string, TraceState>(StringComparer.Ordinal);
            foreach (var series in session.Series)
            {
                var live = existing.GetValueOrDefault(series.Trace.Key);
                var state = new TraceState(series.Trace)
                {
                    Selected = series.Selected,
                    Imported = true,
                    Available = live?.Available ?? false,
                    LastSeen = live?.LastSeen ?? DateTimeOffset.MinValue,
                    StaleAfter = live?.StaleAfter ?? TimeSpan.FromSeconds(5)
                };
                state.Samples.AddRange(series.Samples);
                imported.Add(series.Trace.Key, state);
            }

            var colors = imported.Values.Select(state => state.Trace.Color).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var colorIndex = imported.Count;
            foreach (var live in existing.Values.Where(state => state.Available && !imported.ContainsKey(state.Trace.Key)))
            {
                var trace = live.Trace;
                while (!colors.Add(trace.Color))
                {
                    var sample = new SensorReading(trace.Identity, SensorUnits.For(trace.Identity.Metric), 0, _clock.GetUtcNow());
                    var definition = PerformanceTrace.Create(sample, trace.Source, colorIndex++)!;
                    trace = trace with { Color = definition.Color };
                }

                imported.Add(live.Trace.Key, new TraceState(trace)
                {
                    Available = true,
                    LastSeen = live.LastSeen,
                    StaleAfter = live.StaleAfter
                });
            }

            _traces.Clear();
            foreach (var (key, state) in imported)
            {
                _traces.Add(key, state);
            }

            _sessionId = Guid.NewGuid().ToString("N");
            _startedAt = session.StartedAt;
            _duration = session.DurationSeconds;
            _sampleCount = session.SampleCount;
            _status = "Imported recording.";
        }

        Changed?.Invoke();
    }

    private void RefreshAvailability()
    {
        var now = _clock.GetUtcNow();
        foreach (var state in _traces.Values.Where(state => state.Available && now - state.LastSeen > state.StaleAfter))
        {
            state.Available = false;
            AddGap(state);
        }
    }

    private void MarkUnavailable()
    {
        _activeMsiConnectors.Clear();
        foreach (var state in _traces.Values)
        {
            state.Available = false;
            AddGap(state);
        }
    }

    private void AddGap(TraceState state)
    {
        if (!_recording || !state.HasOpenLine)
        {
            return;
        }

        var seconds = state.Samples.Count == 0 ? 0 : state.Samples[^1].Seconds;
        state.Samples.Add(new(seconds, null));
        state.HasOpenLine = false;
    }

    private void UpdateDuration()
    {
        if (_recording)
        {
            _duration = Math.Max(_duration, _clock.GetElapsedTime(_startedTimestamp).TotalSeconds);
        }
    }

    private void StopCore(string message)
    {
        UpdateDuration();
        _recording = false;
        _status = message;
    }

    private sealed class TraceState(PerformanceTrace trace)
    {
        public PerformanceTrace Trace { get; } = trace;
        public bool Selected { get; set; }
        public bool Imported { get; set; }
        public bool Available { get; set; }
        public DateTimeOffset LastSeen { get; set; }
        public TimeSpan StaleAfter { get; set; }
        public DateTimeOffset? LastRecorded { get; set; }
        public bool HasOpenLine { get; set; }
        public List<PerformanceSample> Samples { get; } = [];
        public (int Count, double Start, double End, int Width, PerformanceSample[] Points)? DisplayCache { get; set; }
    }
}
