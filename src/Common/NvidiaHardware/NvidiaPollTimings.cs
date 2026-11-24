using System.Diagnostics;

namespace RTXLens.Hardware.Nvidia;

public sealed class NvidiaPollTimings
{
    private readonly Dictionary<string, TimeSpan> _durations = [];

    public void Clear() => _durations.Clear();

    internal Measurement Measure(string operation) => new(this, operation);

    internal readonly struct Measurement(NvidiaPollTimings owner, string operation) : IDisposable
    {
        private readonly long _started = Stopwatch.GetTimestamp();

        public void Dispose() => owner._durations[operation] = Stopwatch.GetElapsedTime(_started);
    }

    public override string ToString() => string.Join(", ", _durations.Select(pair => $"{pair.Key} {pair.Value.TotalMilliseconds:F1} ms"));
}
