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
    public bool IsGpuDiagnostic() => Component is SensorComponent.GpuPcie or SensorComponent.GpuPerformance or SensorComponent.GpuPowerLimits;

    public string Key => string.Join('/', Uri.EscapeDataString(PluginId), Uri.EscapeDataString(DeviceId),
        JsonNamingPolicy.CamelCase.ConvertName(Component.ToString()), Uri.EscapeDataString(ConnectorId ?? "default"),
        Channel?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "all",
        JsonNamingPolicy.CamelCase.ConvertName(Metric.ToString()));
}