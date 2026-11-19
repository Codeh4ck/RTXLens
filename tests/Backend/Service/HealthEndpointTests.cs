using RTXLens.Tests.Infrastructure;

namespace RTXLens.Tests.Service;

internal static class HealthEndpointTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        foreach (var endpoint in new[] { "/health", "/plugins" })
        {
            await runner.RunAsync($"Reading {endpoint} preserves the telemetry sequence", () => PreservesSequenceAsync(endpoint));
        }
        await runner.RunAsync(ReportsServiceLivenessAsync);
        await runner.RunAsync(ReportsTheRunningServiceVersionAsync);
        await runner.RunAsync(RunsWithoutRegisteredPluginsAsync);
    }

    private static async Task PreservesSequenceAsync(string endpoint)
    {
        await using var host = await MonitoringTestHost.StartWithTestPluginsAsync();
        await host.StopSnapshotBroadcastAsync();
        var now = DateTimeOffset.UtcNow;
        var before = host.SensorStore.Snapshot(now, []);
        using var document = System.Text.Json.JsonDocument.Parse(await host.Client.GetStringAsync(endpoint));
        TestAssert.Equal(before.Plugins.Length, document.RootElement.GetProperty("plugins").GetArrayLength());
        var after = host.SensorStore.Snapshot(now, []);
        TestAssert.Equal(before.Sequence + 1, after.Sequence);
    }

    private static async Task ReportsTheRunningServiceVersionAsync()
    {
        await using var host = await MonitoringTestHost.StartWithoutPluginsAsync();
        using var document = System.Text.Json.JsonDocument.Parse(await host.Client.GetStringAsync("/version"));
        var version = document.RootElement.GetProperty("version").GetString();
        TestAssert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        TestAssert.True(Contracts.Updates.SemanticVersion.TryParse(version, out _), "The endpoint must report a stable binary version.");
    }

    private static async Task ReportsServiceLivenessAsync()
    {
        await using var host = await MonitoringTestHost.StartWithTestPluginsAsync();

        var health = await host.GetHealthAsync();
        var status = health.GetProperty("status").GetString();

        TestAssert.Equal("running", status);
    }

    private static async Task RunsWithoutRegisteredPluginsAsync()
    {
        await using var host = await MonitoringTestHost.StartWithoutPluginsAsync();

        var health = await host.GetHealthAsync();
        var pluginCount = health.GetProperty("plugins").GetArrayLength();

        TestAssert.Equal(0, pluginCount);
        TestAssert.Equal("running", health.GetProperty("status").GetString());
    }
}
