using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RTXLens.Client.Http;
using RTXLens.Contracts.Configuration;
using RTXLens.Plugins.Msi.Configuration;
using RTXLens.Plugins.Msi.Contracts.Configuration;
using RTXLens.Plugins.Msi.Control;
using RTXLens.Plugins.Msi.Device;
using RTXLens.Tests.Infrastructure;

namespace RTXLens.Tests.Service;

internal static class MsiCurveHardwareVerification
{
    public static async Task RunAsync()
    {
        await using var host = await MonitoringTestHost.StartWithMsiHardwareAsync();
        var api = new MonitoringApiClient(host.Client);
        var initial = await api.GetMsiSnapshotAsync(default);
        var curve = new MsiFanCurve
        {
            Points = [new(0, 37), new(60, 73), new(85, 100)],
            ResponseDelaySeconds = 0
        };
        var settings = MsiSettings.WithOptions(new PluginSettings(), new MsiOptions
        {
            FanControl = MsiFanControl.Curve,
            FanCurve = curve
        });

        try
        {
            await host.SetPluginSettingsAsync("msi-psu", settings);
            await TestWait.UntilAsync(async () =>
            {
                var live = await api.GetMsiSnapshotAsync(default);
                var expected = MsiFanCurveController.Interpolate(curve.Points, live.TemperatureC);
                return live is { FanControllerState: "Curve", AutomaticFan: false }
                       && live.TargetFanDutyPercent == expected && live.FanDutyPercent == expected;
            }, "hardware curve controller and duty readback");

            // The host continues without a desktop client or editor.
            await Task.Delay(2500);
            var snapshot = await api.GetMsiSnapshotAsync(default);
            TestAssert.Equal("Curve", snapshot.FanControllerState);
            TestAssert.True(snapshot.FanRpm > 0, "The physical fan must report RPM.");
            var stored = await File.ReadAllTextAsync(host.ConfigurationPath);
            TestAssert.True(stored.Contains("fanCurve"), "Curve points must be persisted by the service.");
            Console.WriteLine("PASS: Backend curve evaluates physical temperature and applies matching duty without a desktop app.");
            Console.WriteLine(JsonSerializer.Serialize(snapshot));

            await host.SetPluginSettingsAsync("msi-psu", settings with { Enabled = false });
            using var reader = new MsiPsuReader(NullLogger<MsiPsuReader>.Instance);
            var restored = reader.Read(new MsiOptions(), DateTimeOffset.UtcNow, default)!;
            TestAssert.True(restored is { AutomaticFan: true, FanDutyPercent: >= 0 and <= 100 }, "Plugin disable must restore automatic control.");
            Console.WriteLine("PASS: Disabling the curve plugin restored and verified Automatic.");
        }
        finally
        {
            await host.SetPluginSettingsAsync("msi-psu", settings with { Enabled = false });
        }

        Console.WriteLine($"Verified model: {initial.Model}, revision {initial.Revision}.");
    }
}
