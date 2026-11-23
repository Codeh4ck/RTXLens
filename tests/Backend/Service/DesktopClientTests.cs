using System.Net;
using System.Net.Http.Json;
using RTXLens.Client.Http;
using RTXLens.Client.Serialization;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Discovery;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Monitoring;
using RTXLens.Tests.Infrastructure;
using RTXLens.Tests.TestDoubles;

namespace RTXLens.Tests.Service;

internal static class DesktopClientTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync(DiscoversDisabledPluginsAndTheirCapabilitiesAsync);
        await runner.RunAsync(StreamsBothPluginsThroughTheTypedClientAsync);
        await runner.RunAsync(RejectsAStaleDesktopConfigurationWithoutChangingTheFileAsync);
        await runner.RunAsync(OnlyOneConcurrentVersionedWriteSucceedsAsync);
        await runner.RunAsync(RejectsInvalidPreconditionsAndHonorsPluginSettingsVersionsAsync);
        await runner.RunAsync(DoesNotReuseVersionsAcrossServiceInstancesAsync);
    }

    private static async Task DiscoversDisabledPluginsAndTheirCapabilitiesAsync()
    {
        await using var host = await MonitoringTestHost.StartWithInstalledPluginsDisabledAsync();
        var client = new MonitoringApiClient(host.Client);
        var catalog = await client.GetPluginsAsync(CancellationToken.None);
        TestAssert.Equal(1, catalog.SchemaVersion);
        TestAssert.Equal(4, catalog.Plugins.Length);
        TestAssert.True(catalog.Plugins.All(plugin => plugin is { Enabled: false, State: PluginState.Disabled }), "Installed disabled plugins must remain discoverable.");
        TestAssert.True(catalog.Plugins.Single(plugin => plugin.Id == "wireview-pro2").Capabilities.Contains(PluginCapability.FaultClear), "WireView must advertise fault-clear support.");
        TestAssert.True(catalog.Plugins.Single(plugin => plugin.Id == "asus-astral").Capabilities.Contains(PluginCapability.DeviceSelection), "ASUS must advertise device selection.");
        TestAssert.True(catalog.Plugins.Single(plugin => plugin.Id == "msi-psu").Capabilities.Contains(PluginCapability.DeviceSelection), "MSI must advertise device selection.");
    }

    private static async Task StreamsBothPluginsThroughTheTypedClientAsync()
    {
        await using var host = await MonitoringTestHost.StartWithAsusAsync(new AsusTestState(), new WireViewTestState());
        var client = new MonitoringApiClient(host.Client);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await foreach (var snapshot in client.ReadSensorsAsync(timeout.Token))
        {
            if (snapshot.Sensors.Select(sensor => sensor.Identity.PluginId).Distinct().Count() != 2)
            {
                continue;
            }

            TestAssert.True(snapshot.Sensors.All(sensor => Enum.IsDefined(sensor.Unit) && Enum.IsDefined(sensor.Identity.Metric)), "Typed client must deserialize the existing enum payloads.");
            TestAssert.Equal(0, snapshot.Alerts.Length);
            return;
        }
        throw new InvalidOperationException("Both plugin streams were not received.");
    }

    private static async Task RejectsAStaleDesktopConfigurationWithoutChangingTheFileAsync()
    {
        await using var host = await MonitoringTestHost.StartWithoutPluginsAsync();
        var client = new MonitoringApiClient(host.Client);
        var initial = await client.GetConfigurationAsync(CancellationToken.None);
        var updated = await client.SetConfigurationAsync(initial.Configuration with
        {
            Monitoring = new MonitoringOptions { StreamIntervalMs = 200 }
        }, initial.Version!, CancellationToken.None);
        var file = await File.ReadAllTextAsync(host.ConfigurationPath);
        try
        {
            await client.SetConfigurationAsync(initial.Configuration, initial.Version!, CancellationToken.None);
            throw new InvalidOperationException("A stale write was accepted.");
        }
        catch (MonitoringApiException exception)
        {
            TestAssert.Equal(HttpStatusCode.PreconditionFailed, exception.StatusCode);
        }
        TestAssert.Equal(file, await File.ReadAllTextAsync(host.ConfigurationPath));
        TestAssert.Equal(updated.Version, (await client.GetConfigurationAsync(CancellationToken.None)).Version);
        using var response = await host.Client.GetAsync("/configuration");
        TestAssert.Equal($"\"{updated.Version}\"", response.Headers.ETag?.Tag);
    }

    private static async Task OnlyOneConcurrentVersionedWriteSucceedsAsync()
    {
        await using var host = await MonitoringTestHost.StartWithoutPluginsAsync();
        var client = new MonitoringApiClient(host.Client);
        var initial = await client.GetConfigurationAsync(CancellationToken.None);
        var results = await Task.WhenAll(WriteAsync(200), WriteAsync(300));
        TestAssert.Equal(1, results.Count(status => status == HttpStatusCode.OK));
        TestAssert.Equal(1, results.Count(status => status == HttpStatusCode.PreconditionFailed));
        return;

        async Task<HttpStatusCode> WriteAsync(int interval)
        {
            try
            {
                await client.SetConfigurationAsync(initial.Configuration with
                {
                    Monitoring = new MonitoringOptions { StreamIntervalMs = interval }
                }, initial.Version!, CancellationToken.None);
                return HttpStatusCode.OK;
            }
            catch (MonitoringApiException exception) { return exception.StatusCode; }
        }
    }

    private static async Task RejectsInvalidPreconditionsAndHonorsPluginSettingsVersionsAsync()
    {
        await using var host = await MonitoringTestHost.StartWithInstalledPluginsDisabledAsync();
        var client = new MonitoringApiClient(host.Client);
        var initial = await client.GetConfigurationAsync(CancellationToken.None);
        using var invalid = new HttpRequestMessage(HttpMethod.Put, "/configuration")
        {
            Content = JsonContent.Create(initial.Configuration, ClientJsonContext.Default.ServiceConfiguration)
        };
        invalid.Headers.TryAddWithoutValidation("If-Match", "not-quoted");
        using var invalidResponse = await host.Client.SendAsync(invalid);
        TestAssert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
        using var stale = new HttpRequestMessage(HttpMethod.Put, "/plugins/wireview-pro2/settings")
        {
            Content = JsonContent.Create(new PluginSettings { Enabled = false }, ClientJsonContext.Default.PluginSettings)
        };
        stale.Headers.TryAddWithoutValidation("If-Match", "\"stale-version\"");
        using var staleResponse = await host.Client.SendAsync(stale);
        TestAssert.Equal(HttpStatusCode.PreconditionFailed, staleResponse.StatusCode);
    }

    private static async Task DoesNotReuseVersionsAcrossServiceInstancesAsync()
    {
        await using var first = await MonitoringTestHost.StartWithoutPluginsAsync();
        await using var second = await MonitoringTestHost.StartWithoutPluginsAsync();
        var firstVersion = (await new MonitoringApiClient(first.Client).GetConfigurationAsync(CancellationToken.None)).Version;
        var secondVersion = (await new MonitoringApiClient(second.Client).GetConfigurationAsync(CancellationToken.None)).Version;
        TestAssert.True(firstVersion != secondVersion, "A restarted service must invalidate clients' preconditions even if the revision restarts at one.");
    }
}
