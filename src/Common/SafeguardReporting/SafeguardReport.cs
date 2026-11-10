using RTXLens.Plugins.Msi.Contracts.Safeguard;

namespace RTXLens.SafeguardReporting;

internal sealed record SafeguardReport(Guid Id, string ScriptId, DateTimeOffset StartedAt, int? Connector, int? DelayMs, string Status, SafeguardTelemetry? Telemetry)
{
    public string MachineName { get; init; } = Environment.MachineName;
    public DateTimeOffset? FinishedAt { get; init; }
    public int? ScriptVersion { get; init; }
    public SafeguardStep[] Steps { get; init; } = [];
    public bool IsTestEmail { get; init; }
    public SafeguardMailSettings? MailSettings { get; init; }
    public SafeguardDelivery? Delivery { get; init; }

    public static SafeguardReport ForExecution(SafeguardExecution execution, string status, SafeguardTelemetry? telemetry) =>
        new(execution.Id, execution.ScriptId, execution.StartedAt, execution.Connector, execution.DelayMs, status, telemetry)
        {
            FinishedAt = execution.FinishedAt,
            ScriptVersion = execution.Version,
            Steps = execution.Steps
        };
}
