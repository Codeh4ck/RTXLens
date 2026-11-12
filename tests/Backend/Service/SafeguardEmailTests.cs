using MimeKit;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Plugins.Msi.Contracts.Safeguard;
using RTXLens.Service.Services;
using RTXLens.SafeguardReporting;
using RTXLens.Tests.Infrastructure;

namespace RTXLens.Tests.Service;

internal static class SafeguardEmailTests
{
    private static readonly DateTimeOffset Trigger = new(2026, 11, 9, 2, 45, 47, TimeSpan.Zero);

    public static void Run(TestRunner runner)
    {
        runner.Run(CapturesOnlyRecentReadingsAtOrBeforeTheTrigger);
        runner.Run(BuildsEscapedHtmlAndPlainTextWithSensorSources);
        runner.Run(LabelsSimulationAndReportsMissingTelemetry);
        runner.Run(GroupsPinMetricsAndUsesTheSameChartsForTestEmail);
    }

    private static void CapturesOnlyRecentReadingsAtOrBeforeTheTrigger()
    {
        var before = Trigger.AddMilliseconds(-200);
        var after = Trigger.AddMilliseconds(200);
        var buffer = new SafeguardTelemetryBuffer();
        buffer.Observe(new(1, 1, before.AddSeconds(-1), [], [], []));
        buffer.Observe(new(1, 2, before,
        [
            Reading("asus-astral", SensorComponent.GpuPin, SensorMetric.Current, 8.3, before),
            Reading("wireview-pro2", SensorComponent.GpuPin, SensorMetric.Current, 8.4, after)
        ], [], []));
        buffer.Observe(new(1, 3, after, [], [], []));

        var captured = buffer.CaptureAt(Trigger)!;
        TestAssert.Equal(before, captured.CapturedAt);
        TestAssert.Equal(8.3, captured.Readings.Single().Value!.Value);
        TestAssert.True(buffer.CaptureAt(before.AddSeconds(-2)) is null, "A later snapshot cannot stand in for an earlier trigger.");
        TestAssert.True(buffer.CaptureAt(after.AddSeconds(31)) is null, "An old frame cannot stand in for a fresh trigger.");
    }

    private static void BuildsEscapedHtmlAndPlainTextWithSensorSources()
    {
        var frame = new SafeguardTelemetry(Trigger,
        [
            Reading("wireview-pro2", SensorComponent.GpuPin, SensorMetric.Current, 8.1, Trigger),
            Reading("asus-astral", SensorComponent.GpuPin, SensorMetric.Current, 8.2, Trigger),
            Reading("msi-psu", SensorComponent.PsuPin, SensorMetric.Current, 0, Trigger, "1"),
            Reading("msi-psu", SensorComponent.PsuPin, SensorMetric.Current, 8.3, Trigger, "2"),
            Reading("generic-nvidia", SensorComponent.GpuCore, SensorMetric.Voltage, 0.815, Trigger),
            Reading("generic-nvidia", SensorComponent.GpuMemory, SensorMetric.MemoryUsed, 2048, Trigger),
            Reading("generic-nvidia", SensorComponent.GpuHotspot, SensorMetric.Temperature, 99, Trigger) with { Quality = ReadingQuality.Stale }
        ]);
        var (html, plain) = CreateBodies(SafeguardScripts.Notify, frame);
        TestAssert.True(html.Contains("WireView Pro II") && html.Contains("ROG Astral") && html.Contains("MSI PSU")
            && html.Contains("Connector 1 pins") && html.Contains("Connector 2 pins") && html.Contains("NVIDIA GPU status"),
            "The report must show the source and connector of each reading.");
        TestAssert.True(html.Contains("8.10 A") && html.Contains("8.20 A") && html.Contains("8.30 A") && html.Contains("0.00 A"),
            "All sources, including valid zero readings, must be shown.");
        TestAssert.True(plain.Contains("815.0 mV") && plain.Contains("2.00 GB") && plain.Contains("Hotspot temperature: Stale")
            && !plain.Contains("99.0"), "GPU status must use readable units and identify stale readings.");
        TestAssert.True(plain.Contains("Nov 2026 at") && plain.Contains("(UTC") && !plain.Contains("2026-11-09T"),
            "Triggered time must be readable and identify the time zone.");
        TestAssert.True(html.Contains("&lt;device&gt;") && html.Contains("&lt;machine&gt;") && html.Contains("&lt;step&gt;")
            && !html.Contains("<device>") && !html.Contains("<machine>") && !html.Contains("<step>"),
            "Device names, machine name and execution details must be encoded.");
    }

    private static void LabelsSimulationAndReportsMissingTelemetry()
    {
        var (html, plain) = CreateBodies(SafeguardScripts.Simulation, null);
        TestAssert.True(html.Contains("Safeguard+ simulation") && html.Contains("Validation only")
            && plain.Contains("Simulated shutdown delay"), "The recipient must be able to tell the alarm was simulated.");
        TestAssert.True(plain.Contains("No sensor readings were captured at or before this trigger."),
            "A missing capture must be explicit.");
    }

    private static void GroupsPinMetricsAndUsesTheSameChartsForTestEmail()
    {
        var frame = new SafeguardTelemetry(Trigger,
        [
            Reading("wireview-pro2", SensorComponent.GpuPin, SensorMetric.Voltage, 12.05, Trigger),
            Reading("wireview-pro2", SensorComponent.GpuPin, SensorMetric.Power, 90, Trigger),
            Reading("wireview-pro2", SensorComponent.GpuPin, SensorMetric.Current, 7.5, Trigger),
            Reading("wireview-pro2", SensorComponent.GpuPin, SensorMetric.Current, 8.1, Trigger) with
            {
                Identity = new("wireview-pro2", "<device>", SensorComponent.GpuPin, SensorMetric.Current, 2)
            }
        ]);
        var report = new SafeguardReport(Guid.NewGuid(), SafeguardScripts.Notify, Trigger, 2, 5000, "Triggered", frame);
        var real = (MultipartAlternative)SafeguardJournalService.CreateReportBody(report);
        var test = (MultipartAlternative)SafeguardJournalService.CreateReportBody(report with { IsTestEmail = true, Status = "SMTP test" });
        var current = test.TextBody!.IndexOf("Pins · Current", StringComparison.Ordinal);
        var power = test.TextBody!.IndexOf("Pins · Power", StringComparison.Ordinal);
        var voltage = test.TextBody!.IndexOf("Pins · Voltage", StringComparison.Ordinal);

        TestAssert.True(current >= 0 && current < power && power < voltage, "Report charts must be grouped and ordered by metric.");
        TestAssert.True(test.TextBody!.Contains("P1: 7.50 A") && test.TextBody.Contains("P2: 8.10 A")
            && test.TextBody.Contains("P1: 90 W") && test.TextBody.Contains("P1: 12.05 V"), "The test report must use the same measured values.");
        TestAssert.True(test.HtmlBody!.IndexOf("TEST EMAIL", StringComparison.Ordinal)
            < test.HtmlBody.IndexOf("<h1", StringComparison.Ordinal), "The test banner must appear above the report title.");
        var realHtml = real.HtmlBody!;
        var chartStart = realHtml.IndexOf("Sensor snapshot", StringComparison.Ordinal);
        TestAssert.True(chartStart >= 0 && test.HtmlBody.EndsWith(realHtml[chartStart..], StringComparison.Ordinal),
            "The test and real reports must render identical sensor sections.");
    }

    private static (string Html, string Plain) CreateBodies(string scriptId, SafeguardTelemetry? frame)
    {
        var report = new SafeguardReport(Guid.NewGuid(), scriptId, Trigger, 2, 5000, "Triggered", frame)
        {
            MachineName = "<machine>",
            Steps = [new(Trigger, "<step>", "Result")]
        };
        var body = (MultipartAlternative)SafeguardJournalService.CreateReportBody(report);
        return (body.HtmlBody!, body.TextBody!);
    }

    private static SensorReading Reading(string plugin, SensorComponent component, SensorMetric metric, double value,
        DateTimeOffset timestamp, string? connector = null) =>
        new(new(plugin, "<device>", component, metric, component is SensorComponent.GpuPin or SensorComponent.PsuPin ? 1 : null, connector),
            SensorUnits.For(metric), value, timestamp);
}
