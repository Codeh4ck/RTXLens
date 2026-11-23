using System.Net;
using System.Text.Json;
using RTXLens.Contracts.Configuration;
using RTXLens.Client.Http;
using RTXLens.Contracts.Enums;
using RTXLens.Plugins.Msi.Configuration;
using RTXLens.Plugins.Msi.Contracts.Configuration;
using RTXLens.Tests.Infrastructure;
using RTXLens.Tests.TestDoubles;

namespace RTXLens.Tests.Service;

internal static class MsiEndpointTests
{
    public static async Task RunAsync(TestRunner runner) => await runner.RunAsync(PublishesAndReconfiguresMsiAsync);

    private static async Task PublishesAndReconfiguresMsiAsync()
    {
        var state = new MsiTestState();
        await using var host = await MonitoringTestHost.StartWithMsiAsync(state);
        var snapshot = await host.GetSensorSnapshotAsync();
        TestAssert.Equal(18, snapshot.GetProperty("sensors").GetArrayLength());
        TestAssert.True(snapshot.GetProperty("sensors").EnumerateArray().All(sensor =>
            sensor.GetProperty("identity").GetProperty("pluginId").GetString() == "msi-psu"), "MSI readings must be namespaced.");
        var options = MsiSettings.WithOptions(new PluginSettings(), new MsiOptions { FanControl = MsiFanControl.Manual, FanDutyPercent = 75 });
        await host.SetPluginSettingsAsync("msi-psu", options);
        await TestWait.UntilAsync(() => Task.FromResult(state.LastOptions?.FanDutyPercent == 75), "MSI settings applied");
        var response = await host.Client.GetStringAsync("/plugins/msi-psu/device");
        using var data = JsonDocument.Parse(response);
        TestAssert.Equal(75, data.RootElement.GetProperty("fanDutyPercent").GetInt32());
        var client = new MonitoringApiClient(host.Client);
        TestAssert.Equal(1, (await client.GetMsiDevicesAsync(CancellationToken.None)).Length);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var frame in client.ReadSensorsAsync(timeout.Token))
        {
            if (frame.Sensors.Single(sensor => sensor.Identity.Metric == SensorMetric.FanDuty).Value != 75)
            {
                continue;
            }

            TestAssert.Equal(12, frame.Sensors.Count(sensor => sensor.Identity.Component == SensorComponent.PsuPin));
            break;
        }
        await host.SetPluginSettingsAsync("msi-psu", options with { Enabled = false });
        TestAssert.True(state.ReleaseCount > 0, "Disabling must release owned fan control.");
        using var disabled = await host.Client.GetAsync("/plugins/msi-psu/device");
        TestAssert.Equal(HttpStatusCode.ServiceUnavailable, disabled.StatusCode);
        var curveOptions = new MsiOptions
        {
            FanControl = MsiFanControl.Curve,
            FanCurve = new MsiFanCurve { Points = [new(30, 37), new(75, 100)], ResponseDelaySeconds = 8 }
        };
        var curveSettings = MsiSettings.WithOptions(options, curveOptions);
        await host.SetPluginSettingsAsync("msi-psu", curveSettings);
        var settingsJson = await host.Client.GetStringAsync("/plugins/msi-psu/settings");
        var roundTrip = JsonSerializer.Deserialize(settingsJson,
            RTXLens.Service.AppJsonContext.Default.PluginSettings);
        TestAssert.True(roundTrip is not null && MsiSettings.GetOptions(roundTrip).FanCurve.Matches(curveOptions.FanCurve),
            "Curve settings must round-trip through the service API.");

        var invalid = options with { PollIntervalMs = 100 };
        using var content = new StringContent(JsonSerializer.Serialize(invalid), System.Text.Encoding.UTF8, "application/json");
        using var rejected = await host.Client.PutAsync("/plugins/msi-psu/settings", content);
        TestAssert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }
}
