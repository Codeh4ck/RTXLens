using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;

namespace RTXLens.Contracts.Warnings;

public sealed record WarningIncident(Guid Id, string Kind, string Title, string Source, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string Status)
{
    public static WarningIncident Capture(Guid id, WarningState state, DateTimeOffset capturedAt, SensorReading[] readings, bool includeGpuContext)
    {
        var valid = readings
            .Where(reading => reading is { Quality: ReadingQuality.Valid, Value: { } value } && double.IsFinite(value))
            .ToArray();

        return new(id, "warning", state.Title, state.PluginId, state.StartedAt, null, "Active")
        {
            DeviceId = state.DeviceId,
            Detail = state.Detail,
            CapturedAt = capturedAt,
            Connector = int.TryParse(state.ConnectorId, out var connector) ? connector : null,
            Thresholds = state.Thresholds,
            Readings = [.. valid.Where(reading => reading.Identity.PluginId == state.PluginId && reading.Identity.DeviceId == state.DeviceId).Take(512)],
            GpuReadings = includeGpuContext ? [.. valid.Where(reading => reading.Identity.IsGpuStatus() || reading.Identity.IsGpuDiagnostic()).Take(512)] : []
        };
    }

    public string? Detail { get; init; }
    public string? DeviceId { get; init; }
    public int? Connector { get; init; }
    public int? DelayMs { get; init; }
    public string? DeliveryStatus { get; init; }
    public DateTimeOffset? CapturedAt { get; init; }
    public WarningThreshold[] Thresholds { get; init; } = [];
    public SensorReading[] Readings { get; init; } = [];
    public SensorReading[] GpuReadings { get; init; } = [];
    public IncidentStep[] Steps { get; init; } = [];
}

public sealed record IncidentStep(DateTimeOffset Timestamp, string Name, string Result, int? ExitCode);
