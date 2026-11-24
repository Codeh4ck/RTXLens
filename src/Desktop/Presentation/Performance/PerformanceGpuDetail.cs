using RTXLens.Contracts.Enums;

namespace RTXLens.Presentation.Performance;

public sealed record PerformanceGpuDetail(string DeviceId, SensorMetric Metric, double Value)
{
    public static bool Supports(SensorMetric metric) => metric is SensorMetric.SupportedPerformanceStates
        or SensorMetric.MaximumGpuLinkGeneration or SensorMetric.MaximumSystemLinkGeneration
        or SensorMetric.MaximumLinkWidth or SensorMetric.MaximumLinkSpeed;

    public string Description => Metric switch
    {
        SensorMetric.SupportedPerformanceStates => "Supported P-states: " + string.Join(", ",
            Enumerable.Range(0, 16).Where(index => ((int)Value & (1 << index)) != 0).Select(index => $"P{index}")),
        SensorMetric.MaximumGpuLinkGeneration => $"GPU PCIe maximum: Gen {Value:0}",
        SensorMetric.MaximumSystemLinkGeneration => $"System PCIe maximum: Gen {Value:0}",
        SensorMetric.MaximumLinkWidth => $"Maximum link width: ×{Value:0}",
        SensorMetric.MaximumLinkSpeed => $"Maximum link speed: {Value / 1000:0.##} GT/s per lane",
        _ => ""
    };
}
