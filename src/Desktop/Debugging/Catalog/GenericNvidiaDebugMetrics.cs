using RTXLens.Contracts.Enums;
using RTXLens.Debugging.Models;

namespace RTXLens.Debugging.Catalog;

internal static class GenericNvidiaDebugMetrics
{
    public static DebugMetricDefinition[] Create() =>
    [
        new("GPU", "Core clock", SensorComponent.GpuCore, SensorMetric.Clock, 2400),
        new("GPU", "Memory clock", SensorComponent.GpuMemory, SensorMetric.Clock, 14000),
        new("GPU", "Crossbar clock", SensorComponent.GpuCrossbar, SensorMetric.Clock, 2100),
        new("GPU", "GPU system clock", SensorComponent.GpuSystem, SensorMetric.Clock, 1800),
        new("GPU", "GPU usage", SensorComponent.GpuCore, SensorMetric.Utilization, 65),
        new("GPU", "Core voltage (V)", SensorComponent.GpuCore, SensorMetric.Voltage, 0.925),
        new("Connector", "Power", SensorComponent.GpuConnector, SensorMetric.Power, 432, 1),
        new("Connector", "Current", SensorComponent.GpuConnector, SensorMetric.Current, 36, 1),
        new("Connector", "Voltage", SensorComponent.GpuConnector, SensorMetric.Voltage, 12, 1),
        new("Temperatures", "Core temperature", SensorComponent.GpuCore, SensorMetric.Temperature, 65),
        new("Temperatures", "VRAM temperature", SensorComponent.GpuMemory, SensorMetric.Temperature, 72),
        new("Temperatures", "Hotspot temperature", SensorComponent.GpuHotspot, SensorMetric.Temperature, 78),
        new("Fans", "Fan 1 RPM", SensorComponent.GpuFan, SensorMetric.FanSpeed, 1600, 1),
        new("Fans", "Fan 1 duty", SensorComponent.GpuFan, SensorMetric.FanDuty, 50, 1),
        new("Fans", "Fan 2 RPM", SensorComponent.GpuFan, SensorMetric.FanSpeed, 1450, 2),
        new("Fans", "Fan 2 duty", SensorComponent.GpuFan, SensorMetric.FanDuty, 45, 2),
        new("Memory", "VRAM used (MB)", SensorComponent.GpuMemory, SensorMetric.MemoryUsed, 8192),
        new("Memory", "VRAM total (MB)", SensorComponent.GpuMemory, SensorMetric.MemoryTotal, 32768),
        new("Memory", "VRAM free (MB)", SensorComponent.GpuMemory, SensorMetric.MemoryFree, 24576),
        new("Memory", "VRAM usage", SensorComponent.GpuMemory, SensorMetric.Utilization, 25)
    ];
}
