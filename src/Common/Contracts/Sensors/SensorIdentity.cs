using System.Text.Json;
using RTXLens.Contracts.Enums;

namespace RTXLens.Contracts.Sensors;

public sealed record SensorIdentity(
    string PluginId,
    string DeviceId,
    SensorComponent Component,
    SensorMetric Metric,
    int? Channel = null,
    string? ConnectorId = null)
{
    public bool IsGpuStatus() => Component is SensorComponent.GpuBoard or SensorComponent.GpuCore or SensorComponent.GpuMemory
        or SensorComponent.GpuFan or SensorComponent.GpuHotspot or SensorComponent.GpuMemoryChip
        or SensorComponent.GpuCrossbar or SensorComponent.GpuSystem;

    public bool IsGpuDiagnostic() => Component is SensorComponent.GpuPcie or SensorComponent.GpuPerformance or SensorComponent.GpuPowerLimits
        or SensorComponent.GpuCrossbar or SensorComponent.GpuSystem;

    public string Key => string.Join('/', Uri.EscapeDataString(PluginId), Uri.EscapeDataString(DeviceId),
        JsonNamingPolicy.CamelCase.ConvertName(Component.ToString()), Uri.EscapeDataString(ConnectorId ?? "default"),
        Channel?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "all",
        JsonNamingPolicy.CamelCase.ConvertName(Metric.ToString()));
}