using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using RTXLens.Client.Http;
using RTXLens.Contracts.Configuration;
using RTXLens.Tests.Infrastructure;
using RTXLens.Tests.TestDoubles;

namespace RTXLens.Tests.Service;

internal static class WireViewEndpointTests
{
    private const string Prefix = "/plugins/wireview-pro2";

    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync(PreservesTheOriginalHttpSnapshotAsync);
        await runner.RunAsync(BroadcastsNativeConnectorVoltageAsync);
        await runner.RunAsync(KeepsNativeConnectorVoltageAtZeroCurrentAsync);
        await runner.RunAsync(MarksInvalidNativeConnectorVoltageUnavailableAsync);
        await runner.RunAsync(StreamsTheOriginalWireViewPayloadAsync);
        await runner.RunAsync(ReturnsDeviceConfigurationAndRawBytesAsync);
        await runner.RunAsync(SavesWireViewSettingsAndRejectsStaleAndBrowserWritesAsync);
        await runner.RunAsync(ComplementsIndependentFaultMasksAsync);
        await runner.RunAsync(ReportsFailedDeviceOperationsAsync);
        await runner.RunAsync(RestartsOnlyTheWireViewPluginAsync);
        await runner.RunAsync(KeepsSettingsAccessibleWhileDisabledAsync);
        await runner.RunAsync(ReportsDisconnectedDevicesWithoutStoppingTheHostAsync);
        await runner.RunAsync(ClosesLegacySubscribersWhenDisabledAsync);
        await runner.RunAsync(ProvidesAnExplicitHostRestartEndpointAsync);
        await runner.RunAsync(SerializesDeviceCommandsWithPollingAsync);
    }

    private static async Task PreservesTheOriginalHttpSnapshotAsync()
    {
        await using var host = await MonitoringTestHost.StartWithWireViewAsync(new WireViewTestState());
        await host.GetSensorSnapshotAsync();

        using var response = await host.Client.GetAsync(Prefix + "/sensors");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var snapshot = document.RootElement;

        TestAssert.Equal(6, snapshot.GetProperty("pins").GetArrayLength());
        TestAssert.Equal(21d, snapshot.GetProperty("totalCurrentA").GetDouble());
        TestAssert.Equal(252d, snapshot.GetProperty("totalPowerW").GetDouble());
        TestAssert.Equal(-1d, snapshot.GetProperty("extTemp1C").GetDouble());
        TestAssert.Equal(0x08, snapshot.GetProperty("faultStatus").GetInt32());
        TestAssert.False(snapshot.TryGetProperty("schemaVersion", out _), "Legacy snapshots must retain their original shape.");
    }

    private static async Task BroadcastsNativeConnectorVoltageAsync()
    {
        var device = new WireViewTestState();
        device.Sample = device.Sample with { ReportedAverageVoltageV = 12.073, ReportedTotalPowerW = 62.345, ReportedTotalCurrentA = 5 };
        await using var host = await MonitoringTestHost.StartWithWireViewAsync(device);

        var snapshot = await host.GetSensorSnapshotAsync();
        var voltage = FindConnectorVoltage(snapshot);

        TestAssert.Equal("Volts", voltage.GetProperty("unit").GetString());
        TestAssert.Equal("Valid", voltage.GetProperty("quality").GetString());
        TestAssert.Equal(12.073, voltage.GetProperty("value").GetDouble());
        TestAssert.Equal(12d, device.Sample.PinVoltage.Average());

        using var legacy = await host.Client.GetFromJsonAsync<JsonDocument>(Prefix + "/sensors");
        TestAssert.Equal(62.345, legacy!.RootElement.GetProperty("totalPowerW").GetDouble());
        TestAssert.Equal(5d, legacy.RootElement.GetProperty("totalCurrentA").GetDouble());
    }

    private static async Task KeepsNativeConnectorVoltageAtZeroCurrentAsync()
    {
        var device = new WireViewTestState();
        device.Sample = device.Sample with { ReportedAverageVoltageV = 12.08, ReportedTotalPowerW = 0, ReportedTotalCurrentA = 0 };
        await using var host = await MonitoringTestHost.StartWithWireViewAsync(device);

        var voltage = FindConnectorVoltage(await host.GetSensorSnapshotAsync());

        TestAssert.Equal(12.08, voltage.GetProperty("value").GetDouble());
        TestAssert.Equal("Valid", voltage.GetProperty("quality").GetString());
    }

    private static async Task MarksInvalidNativeConnectorVoltageUnavailableAsync()
    {
        foreach (var nativeVoltage in new double?[] { null, 0, -0.001, double.NaN, double.PositiveInfinity })
        {
            var device = new WireViewTestState();
            device.Sample = device.Sample with { ReportedAverageVoltageV = nativeVoltage };
            await using var host = await MonitoringTestHost.StartWithWireViewAsync(device);

            var snapshot = await host.GetSensorSnapshotAsync();
            var voltage = FindConnectorVoltage(snapshot);

            TestAssert.Equal(JsonValueKind.Null, voltage.GetProperty("value").ValueKind);
            TestAssert.Equal("Unavailable", voltage.GetProperty("quality").GetString());
        }
    }

    private static JsonElement FindConnectorVoltage(JsonElement snapshot) => snapshot.GetProperty("sensors").EnumerateArray().Single(reading =>
        reading.GetProperty("identity").GetProperty("pluginId").GetString() == "wireview-pro2"
        && reading.GetProperty("identity").GetProperty("component").GetString() == "GpuConnector"
        && reading.GetProperty("identity").GetProperty("metric").GetString() == "Voltage"
        && reading.GetProperty("identity").GetProperty("channel").GetInt32() == 1);

    private static async Task StreamsTheOriginalWireViewPayloadAsync()
    {
        await using var host = await MonitoringTestHost.StartWithWireViewAsync(new WireViewTestState());
        await host.GetSensorSnapshotAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = await host.ConnectSensorClientAsync(timeout.Token, Prefix + "/ws");

        using var document = await WebSocketMessageReader.ReadJsonAsync(client, timeout.Token);

        TestAssert.Equal(6, document.RootElement.GetProperty("pins").GetArrayLength());
        TestAssert.Equal(600, document.RootElement.GetProperty("psuCapabilityW").GetInt32());
    }

    private static async Task ReturnsDeviceConfigurationAndRawBytesAsync()
    {
        await using var host = await MonitoringTestHost.StartWithWireViewAsync(new WireViewTestState());
        await host.GetSensorSnapshotAsync();

        using var configuration = await host.Client.GetFromJsonAsync<JsonDocument>(Prefix + "/device/config");
        using var raw = await host.Client.GetFromJsonAsync<JsonDocument>(Prefix + "/device/config/raw");

        TestAssert.Equal("Test WireView", configuration!.RootElement.GetProperty("friendlyName").GetString());
        TestAssert.Equal(9.2, configuration.RootElement.GetProperty("wireOcpFaultThresholdA").GetDouble());
        TestAssert.Equal("EF-05-02-41", raw!.RootElement.GetProperty("bytes").GetString());
        TestAssert.Equal(4, raw.RootElement.GetProperty("length").GetInt32());
    }

    private static async Task SavesWireViewSettingsAndRejectsStaleAndBrowserWritesAsync()
    {
        await using var host = await MonitoringTestHost.StartWithWireViewAsync(new WireViewTestState());
        await host.GetSensorSnapshotAsync();
        var api = new MonitoringApiClient(host.Client);

        var initial = await api.GetWireViewConfigurationAsync(CancellationToken.None);
        var changes = initial with { FriendlyName = "Updated WireView" };
        var applied = await api.ApplyWireViewConfigurationAsync(changes, CancellationToken.None);

        TestAssert.Equal("Updated WireView", applied.FriendlyName);
        TestAssert.True(applied.EditToken != initial.EditToken, "Applying changes must produce a new edit token.");

        using var incomplete = await host.Client.PutAsJsonAsync(Prefix + "/device/config",
            new { applied.EditToken, applied.Version, FriendlyName = "Incomplete settings" });
        TestAssert.Equal(HttpStatusCode.BadRequest, incomplete.StatusCode);

        using var stale = await host.Client.PutAsJsonAsync(Prefix + "/device/config", initial);
        TestAssert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);

        using var fromBrowser = new HttpRequestMessage(HttpMethod.Put, Prefix + "/device/config")
        {
            Content = JsonContent.Create(applied with { FriendlyName = "Browser write" })
        };
        fromBrowser.Headers.Add("Origin", "https://example.com");

        using var forbidden = await host.Client.SendAsync(fromBrowser);
        TestAssert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        TestAssert.Equal(applied, await api.GetWireViewConfigurationAsync(CancellationToken.None));
    }

    private static async Task ComplementsIndependentFaultMasksAsync()
    {
        var device = new WireViewTestState();
        await using var host = await MonitoringTestHost.StartWithWireViewAsync(device);
        await host.GetSensorSnapshotAsync();

        using var response = await host.Client.PostAsJsonAsync(Prefix + "/faults/clear", new { faultStatusMask = 0x08, faultLogMask = 0x20 });

        TestAssert.Equal(HttpStatusCode.OK, response.StatusCode);
        TestAssert.Equal((ushort)0xFFF7, device.LastStatusMask);
        TestAssert.Equal((ushort)0xFFDF, device.LastLogMask);
    }

    private static async Task ReportsFailedDeviceOperationsAsync()
    {
        var device = new WireViewTestState();
        await using var host = await MonitoringTestHost.StartWithWireViewAsync(device);
        await host.GetSensorSnapshotAsync();
        device.ConfigReadFails = true;
        device.FaultWriteFails = true;

        using var configuration = await host.Client.GetAsync(Prefix + "/device/config");
        using var raw = await host.Client.GetAsync(Prefix + "/device/config/raw");
        using var faults = await host.Client.PostAsJsonAsync(Prefix + "/faults/clear", new { faultStatusMask = 8 });

        TestAssert.Equal(HttpStatusCode.InternalServerError, configuration.StatusCode);
        TestAssert.Equal(HttpStatusCode.InternalServerError, raw.StatusCode);
        TestAssert.Equal(HttpStatusCode.InternalServerError, faults.StatusCode);
    }

    private static async Task RestartsOnlyTheWireViewPluginAsync()
    {
        var device = new WireViewTestState();
        await using var host = await MonitoringTestHost.StartWithWireViewAsync(device);
        await host.GetSensorSnapshotAsync();
        var before = await host.GetHealthAsync();

        using var response = await host.Client.PostAsync(Prefix + "/restart", content: null);
        response.EnsureSuccessStatusCode();

        var after = await host.GetHealthAsync();
        TestAssert.Equal(before.GetProperty("startedAt").GetString(), after.GetProperty("startedAt").GetString());
        TestAssert.Equal(2, device.CreateCount);
        TestAssert.Equal(1, device.DisposeCount);
        TestAssert.Equal(0, host.RestartRequests);
    }

    private static async Task KeepsSettingsAccessibleWhileDisabledAsync()
    {
        await using var host = await MonitoringTestHost.StartWithWireViewAsync(new WireViewTestState());
        await host.SetPluginSettingsAsync("wireview-pro2", new PluginSettings { Enabled = false });

        using var configuration = await host.Client.GetAsync(Prefix + "/config");
        using var sensors = await host.Client.GetAsync(Prefix + "/sensors");
        using var health = await host.Client.GetFromJsonAsync<JsonDocument>(Prefix + "/health");

        TestAssert.Equal(HttpStatusCode.OK, configuration.StatusCode);
        TestAssert.Equal(HttpStatusCode.ServiceUnavailable, sensors.StatusCode);
        TestAssert.Equal("disabled", health!.RootElement.GetProperty("status").GetString());
    }

    private static async Task ReportsDisconnectedDevicesWithoutStoppingTheHostAsync()
    {
        var device = new WireViewTestState { Connected = false };
        await using var host = await MonitoringTestHost.StartWithWireViewAsync(device);

        using var response = await host.Client.GetAsync(Prefix + "/sensors");
        var health = await host.GetHealthAsync();

        TestAssert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        TestAssert.Equal("running", health.GetProperty("status").GetString());
    }

    private static async Task ClosesLegacySubscribersWhenDisabledAsync()
    {
        await using var host = await MonitoringTestHost.StartWithWireViewAsync(new WireViewTestState());
        await host.GetSensorSnapshotAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = await host.ConnectSensorClientAsync(timeout.Token, Prefix + "/ws");
        using var initial = await WebSocketMessageReader.ReadJsonAsync(client, timeout.Token);

        await host.SetPluginSettingsAsync("wireview-pro2", new PluginSettings { Enabled = false });
        await WebSocketMessageReader.WaitForCloseAsync(client, timeout.Token);

        TestAssert.Equal<WebSocketCloseStatus?>(WebSocketCloseStatus.EndpointUnavailable, client.CloseStatus);
    }

    private static async Task SerializesDeviceCommandsWithPollingAsync()
    {
        var device = new WireViewTestState();
        await using var host = await MonitoringTestHost.StartWithWireViewAsync(device, initialPollMs: 100);
        await host.GetSensorSnapshotAsync();

        var commands = Enumerable.Range(0, 20).Select(async _ =>
        {
            using var response = await host.Client.GetAsync(Prefix + "/device/config");
            response.EnsureSuccessStatusCode();
        });
        await Task.WhenAll(commands);

        TestAssert.Equal(0, device.ConcurrentAccessDetected);
    }

    private static async Task ProvidesAnExplicitHostRestartEndpointAsync()
    {
        await using var host = await MonitoringTestHost.StartWithoutPluginsAsync();

        using var response = await host.Client.PostAsync("/restart", content: null);

        TestAssert.Equal(HttpStatusCode.OK, response.StatusCode);
        TestAssert.Equal(1, host.RestartRequests);
    }
}
