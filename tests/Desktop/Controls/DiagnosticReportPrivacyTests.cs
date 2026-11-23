using System.Text.Json;
using RTXLens.App.Platform;
using RTXLens.Contracts.Diagnostics;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Tests.Infrastructure;

namespace RTXLens.UI.Tests.Controls;

internal static class DiagnosticReportPrivacyTests
{
    public static void ExportsOnlyReviewedFields()
    {
        const string secret = "private-user-token-123";
        var now = DateTimeOffset.UtcNow;
        using var device = JsonDocument.Parse(JsonSerializer.Serialize(new[] { new
        {
            hardwareName = "NVIDIA GeForce RTX 5090 " + secret, deviceId = secret, serialNumber = secret
        } }));
        var gpu = SupportReportData.Endpoint("nvidia-devices", device.RootElement);
        var reading = new SensorReading(new SensorIdentity("generic-nvidia", secret, SensorComponent.GpuConnector,
            SensorMetric.Power, 1, secret), SensorUnit.Watts, 120, now);
        var snapshot = new SensorSnapshot(1, 1, now, [reading], [], [], 0);
        var log = new ServiceLogEntry(1, now, ServiceLogLevel.Warning, secret, secret, secret, secret);
        foreach (var (source, plugin) in new[] { ("ASUS RTX ROG Astral GPU", "asus-astral"), ("NVIDIA", "generic-nvidia"),
                     ("MSI PSU", "msi-psu"), ("WireView Pro II", "wireview-pro2"), (secret, "other") })
        {
            var exported = JsonSerializer.SerializeToElement(SupportReportData.Event(log with { Source = source }));
            TestAssert.Equal(plugin, exported.GetProperty("plugin").GetString());
        }
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(file, [secret, "System.InvalidOperationException: " + secret,
                "   at RTXLens.App.Platform.SupportReportData.Event(ServiceLogEntry entry) in C:\\users\\" + secret + ":line 10"]);
            var output = JsonSerializer.Serialize(new { gpu, frame = SupportReportData.Snapshot(snapshot),
                log = SupportReportData.Event(log), crash = SupportReportData.Crash(file, now, true) });
            TestAssert.False(output.Contains(secret, StringComparison.Ordinal), "A private field reached the report.");
            TestAssert.True(output.Contains("RTX 5090") && output.Contains("InvalidOperationException")
                && output.Contains("SupportReportData.Event"), "Useful hardware and crash diagnostics should survive filtering.");
        }
        finally { File.Delete(file); }
    }
}
