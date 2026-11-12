namespace RTXLens.Presentation.Performance;

public sealed partial class PerformanceRecorder
{
    public PerformanceChartFrame ReadDisplayFrame(PerformanceViewport viewport)
    {
        lock (_gate)
        {
            var frame = ReadFrame(null, new Dictionary<string, int>(), includeSamples: false);
            var end = viewport.Follow ? Math.Max(10, frame.DurationSeconds)
                : viewport.Fit ? Math.Max(1, frame.DurationSeconds) : viewport.End;
            var start = viewport.Follow ? Math.Max(0, end - 60) : viewport.Fit ? 0 : viewport.Start;
            var keys = viewport.Keys is null ? null : new HashSet<string>(viewport.Keys, StringComparer.Ordinal);
            var width = Math.Clamp(viewport.Width, 200, 2400);
            foreach (var state in _traces.Values.Where(state => !state.Selected || keys is not null && !keys.Contains(state.Trace.Key)))
            {
                state.DisplayCache = null;
            }

            var series = frame.Series.Select(series => series with
            {
                Offset = 0,
                Samples = series.Selected && (keys is null || keys.Contains(series.Trace.Key))
                    ? CachedDisplaySamples(_traces[series.Trace.Key], start, end, width) : []
            }).ToArray();

            return frame with { Series = series, DisplayOnly = true };
        }
    }

    public PerformanceHover ReadHover(string sessionId, long requestId, double seconds)
    {
        lock (_gate)
        {
            var values = new Dictionary<string, double?>(StringComparer.Ordinal);
            if (sessionId == _sessionId && double.IsFinite(seconds) && seconds >= 0)
            {
                foreach (var state in _traces.Values.Where(IsSelectedForHover))
                {
                    values[state.Trace.Key] = NearestSample(state.Samples, seconds)?.Value;
                }
            }

            return new(_sessionId, requestId, seconds, values);
        }
    }

    /// <summary>Looks up original samples for the requested traces in the same order as their keys.</summary>
    public void ReadHover(string sessionId, double seconds, IReadOnlyList<string> keys, Span<PerformanceSample?> samples)
    {
        samples = samples[..keys.Count];
        samples.Clear();

        lock (_gate)
        {
            if (sessionId != _sessionId || !double.IsFinite(seconds) || seconds < 0)
            {
                return;
            }

            for (var index = 0; index < keys.Count; index++)
            {
                if (_traces.TryGetValue(keys[index], out var state) && IsSelectedForHover(state))
                {
                    samples[index] = NearestSample(state.Samples, seconds);
                }
            }
        }
    }

    private bool IsSelectedForHover(TraceState state) =>
        state.Selected && (state.Imported || _settings.GetValueOrDefault(state.Trace.Identity.PluginId)?.Enabled == true);

    private static PerformanceSample? NearestSample(List<PerformanceSample> samples, double seconds)
    {
        var index = LowerBound(samples, seconds);
        if (index == samples.Count)
        {
            return samples.Count == 0 ? null : samples[^1];
        }

        if (samples[index].Seconds == seconds)
        {
            return samples[index];
        }

        if (index == 0 || samples[index - 1].Value is null || samples[index].Value is null)
        {
            return null;
        }

        return seconds - samples[index - 1].Seconds <= samples[index].Seconds - seconds
            ? samples[index - 1] : samples[index];
    }

    private static int LowerBound(List<PerformanceSample> samples, double seconds)
    {
        var low = 0;
        var high = samples.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (samples[middle].Seconds < seconds)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static PerformanceSample[] CachedDisplaySamples(TraceState state, double start, double end, int width)
    {
        if (state.DisplayCache is { } cache && cache.Count == state.Samples.Count
            && cache.Start == start && cache.End == end && cache.Width == width)
        {
            return cache.Points;
        }

        var points = DisplaySamples(state.Samples, start, end, width);
        state.DisplayCache = (state.Samples.Count, start, end, width, points);
        return points;
    }

    private static PerformanceSample[] DisplaySamples(List<PerformanceSample> samples, double start, double end, int width)
    {
        if (samples.Count == 0 || !double.IsFinite(start) || !double.IsFinite(end) || end <= start)
        {
            return [];
        }

        var first = Math.Max(0, LowerBound(samples, start) - 1);
        var last = Math.Min(samples.Count, LowerBound(samples, end) + 1);
        if (last - first <= width * 4)
        {
            return [.. samples.GetRange(first, last - first)];
        }

        // Keep endpoints and extrema per screen column. Gaps break connections
        // within their column so a reduced trace cannot conceal disconnections.
        var result = new List<PerformanceSample>(width * 8);
        var index = first;
        while (index < last)
        {
            var begin = index;
            var pixel = Math.Clamp((int)((samples[index].Seconds - start) / (end - start) * width), 0, width - 1);
            var minimum = index;
            var maximum = index;
            var gap = false;
            while (index < last)
            {
                var nextPixel = Math.Clamp((int)((samples[index].Seconds - start) / (end - start) * width), 0, width - 1);
                if (nextPixel != pixel)
                {
                    break;
                }

                var value = samples[index].Value;
                gap |= value is null;
                if (value is not null)
                {
                    if (samples[minimum].Value is null || value < samples[minimum].Value)
                    {
                        minimum = index;
                    }

                    if (samples[maximum].Value is null || value > samples[maximum].Value)
                    {
                        maximum = index;
                    }
                }
                index++;
            }

            if (gap)
            {
                result.Add(new(samples[begin].Seconds, null));
            }

            foreach (var point in new[] { begin, minimum, maximum, index - 1 }.Distinct().Order())
            {
                if (samples[point].Value is not null)
                {
                    result.Add(samples[point]);
                    if (gap)
                    {
                        result.Add(new(samples[point].Seconds, null));
                    }
                }
            }
        }

        return [.. result];
    }
}
