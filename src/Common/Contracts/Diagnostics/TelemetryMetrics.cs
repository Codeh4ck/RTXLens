using System.Diagnostics.Metrics;

namespace RTXLens.Contracts.Diagnostics;

public static class TelemetryMetrics
{
    private static readonly Meter Meter = new("RTXLens.Telemetry");

    public static readonly Histogram<double> PollDuration = Meter.CreateHistogram<double>("rtxlens.poll.duration", "ms");
    public static readonly Counter<long> PollOverruns = Meter.CreateCounter<long>("rtxlens.poll.overruns");
    public static readonly Histogram<double> SnapshotDuration = Meter.CreateHistogram<double>("rtxlens.snapshot.duration", "ms");
    public static readonly Counter<long> FramesReplaced = Meter.CreateCounter<long>("rtxlens.stream.frames.replaced");
    public static readonly Counter<long> Reconnects = Meter.CreateCounter<long>("rtxlens.stream.reconnects");
    public static readonly Histogram<double> RenderDuration = Meter.CreateHistogram<double>("rtxlens.chart.render.duration", "ms");
}
