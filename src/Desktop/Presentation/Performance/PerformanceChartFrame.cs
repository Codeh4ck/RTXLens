namespace RTXLens.Presentation.Performance;

public sealed record PerformanceChartFrame(string SessionId, DateTimeOffset? StartedAt, double DurationSeconds,
    bool Recording, long SampleCount, string Status, PerformanceSeries[] Series)
{
    public PerformanceGpuDetail[] GpuDetails { get; init; } = [];
    public bool DisplayOnly { get; init; }
}
