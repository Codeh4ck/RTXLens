using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Monitoring;
using RTXLens.Service.Configuration;
using RTXLens.Tests.Infrastructure;
using RTXLens.Tests.TestDoubles;

namespace RTXLens.Tests.Service;

internal static class RuntimeConfigurationTests
{
    private const string PluginId = "wireview-pro2";

    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync(ReschedulesPollingWithoutWaitingForTheOldIntervalAsync);
        await runner.RunAsync(DisablesAndReenablesPluginsWithoutClosingTheSharedStreamAsync);
        await runner.RunAsync(ReschedulesPublicationWithoutWaitingForTheOldIntervalAsync);
        await runner.RunAsync(AppliesSourceSelectionsToTheNextFrameAsync);
        await runner.RunAsync(MigratesSelectionsOnceAndPreservesEditedProfilesAcrossRestartsAsync);
        await runner.RunAsync(ReloadsAnExternalAtomicFileReplacementAsync);
        await runner.RunAsync(RetainsValidSettingsWhenTheFileIsInvalidAsync);
        await runner.RunAsync(RejectsInvalidApiSettingsWithoutChangingTheFileAsync);
        if (OperatingSystem.IsWindows())
        {
            await runner.RunAsync(RetriesTemporaryLocksAndRejectsPersistentLocksAsync);
        }
        await runner.RunAsync(RollsBackAnActivationFailureAsync);
        await runner.RunAsync(PartialRecoveryPreservesEachFailureAsync);
        await runner.RunAsync(DoesNotWaitForUnchangedHangingPluginsAsync);
        await runner.RunAsync(ReleasesResourcesAcrossRepeatedEnableDisableCyclesAsync);
        await runner.RunAsync(RejectsInvalidExternalSettingsWithoutStoppingThePluginAsync);
    }

    private static async Task ReschedulesPollingWithoutWaitingForTheOldIntervalAsync()
    {
        var device = new WireViewTestState();
        await using var host = await MonitoringTestHost.StartWithWireViewAsync(device, initialPollMs: 30000);
        await host.GetSensorSnapshotAsync();
        var initialPollCount = Volatile.Read(ref device.PollCount);

        using var response = await host.Client.PostAsJsonAsync($"/plugins/{PluginId}/config", new { pollIntervalMs = 100 });
        response.EnsureSuccessStatusCode();
        await TestWait.UntilAsync(() => Task.FromResult(Volatile.Read(ref device.PollCount) > initialPollCount), "the new polling interval");

        var current = await host.GetConfigurationAsync();
        var persisted = RuntimeJson.Deserialize(await File.ReadAllTextAsync(host.ConfigurationPath));
        TestAssert.Equal(100, current.Configuration.Plugins[PluginId].PollIntervalMs);
        TestAssert.Equal(100, persisted.Plugins[PluginId].PollIntervalMs);
        TestAssert.Equal(1, device.CreateCount);
        TestAssert.Equal(0, device.DisposeCount);
    }

    private static async Task DisablesAndReenablesPluginsWithoutClosingTheSharedStreamAsync()
    {
        var device = new WireViewTestState();
        await using var host = await MonitoringTestHost.StartWithWireViewAsync(device);
        var initialHealth = await host.GetHealthAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var client = await host.ConnectSensorClientAsync(timeout.Token);
        await WebSocketMessageReader.ReadUntilAsync(client, frame => frame.GetProperty("sensors").GetArrayLength() > 0, timeout.Token);

        var disabled = new PluginSettings { Enabled = false, PollIntervalMs = 100 };
        var disabledRevision = await host.SetPluginSettingsAsync(PluginId, disabled);
        var disabledFrame = await WebSocketMessageReader.ReadUntilAsync(client,
            frame => frame.GetProperty("configurationRevision").GetInt64() >= disabledRevision, timeout.Token);

        TestAssert.Equal(0, disabledFrame.GetProperty("sensors").GetArrayLength());
        TestAssert.Equal("Disabled", disabledFrame.GetProperty("plugins")[0].GetProperty("state").GetString());
        TestAssert.Equal(1, device.DisposeCount);

        await host.SetPluginSettingsAsync(PluginId, disabled with { Enabled = true });
        await WebSocketMessageReader.ReadUntilAsync(client, frame => frame.GetProperty("sensors").GetArrayLength() > 0, timeout.Token);
        var finalHealth = await host.GetHealthAsync();

        TestAssert.Equal(initialHealth.GetProperty("startedAt").GetString(), finalHealth.GetProperty("startedAt").GetString());
        TestAssert.Equal(2, device.CreateCount);
        TestAssert.Equal(WebSocketState.Open, client.State);
    }

    private static async Task ReschedulesPublicationWithoutWaitingForTheOldIntervalAsync()
    {
        await using var host = await MonitoringTestHost.StartWithoutPluginsAsync();
        var initial = await host.GetConfigurationAsync();
        await host.SetConfigurationAsync(initial.Configuration with { Monitoring = new MonitoringOptions { StreamIntervalMs = 30000 } });
        var slow = await host.GetConfigurationAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = await host.ConnectSensorClientAsync(timeout.Token);
        await WebSocketMessageReader.ReadUntilAsync(client,
            frame => frame.GetProperty("configurationRevision").GetInt64() == slow.Revision, timeout.Token);

        await host.SetConfigurationAsync(slow.Configuration with { Monitoring = new MonitoringOptions { StreamIntervalMs = 100 } });
        var fast = await host.GetConfigurationAsync();
        await WebSocketMessageReader.ReadUntilAsync(client,
            frame => frame.GetProperty("configurationRevision").GetInt64() == fast.Revision, timeout.Token);
    }

    private static async Task AppliesSourceSelectionsToTheNextFrameAsync()
    {
        await using var host = await MonitoringTestHost.StartWithWireViewAsync(new WireViewTestState());
        var current = await host.GetConfigurationAsync();
        var identity = new SensorIdentity(PluginId, "com3", SensorComponent.GpuConnector, SensorMetric.Power, Channel: 1);
        var selection = new SensorSelection { Name = "selected-power", PreferredSources = [identity] };
        var options = new MonitoringOptions { StreamIntervalMs = 100, Selections = [selection] };

        await host.SetConfigurationAsync(current.Configuration with { Monitoring = options });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = await host.ConnectSensorClientAsync(timeout.Token);
        var frame = await WebSocketMessageReader.ReadUntilAsync(client,
            snapshot => snapshot.GetProperty("selections").EnumerateArray().Any(item => item.GetProperty("name").GetString() == selection.Name), timeout.Token);

        var selected = frame.GetProperty("selections").EnumerateArray().Single(item => item.GetProperty("name").GetString() == selection.Name);
        TestAssert.Equal(identity.Key, selected.GetProperty("sensorKey").GetString());
    }

    private static async Task MigratesSelectionsOnceAndPreservesEditedProfilesAcrossRestartsAsync()
    {
        var identity = new SensorIdentity(PluginId, "com3", SensorComponent.GpuConnector, SensorMetric.Power, 1);
        var legacy = new ServiceConfiguration
        {
            Plugins = new() { ["asus-astral"] = new(), [PluginId] = new() },
            Monitoring = new MonitoringOptions { Selections = [new SensorSelection { Name = "old-unused", PreferredSources = [identity] }] }
        };

        ServiceConfiguration edited;
        await using (var first = await MonitoringTestHost.StartWithAsusAsync(new AsusTestState(), new WireViewTestState(), legacy))
        {
            edited = (await first.GetConfigurationAsync()).Configuration;
            TestAssert.Equal(0, edited.Monitoring.Selections.Count);
            TestAssert.Equal(1, edited.Monitoring.ConnectorProfiles.Version);
            var profile = edited.Monitoring.ConnectorProfiles.Profiles[0];
            profile.Name = "My connector preferences";
            profile.Metrics[0].AllowFallback = false;
            await first.SetConfigurationAsync(edited);
            var persisted = RuntimeJson.Deserialize(await File.ReadAllTextAsync(first.ConfigurationPath));
            TestAssert.Equal("My connector preferences", persisted.Monitoring.ConnectorProfiles.Profiles[0].Name);
        }

        await using var second = await MonitoringTestHost.StartWithAsusAsync(new AsusTestState(), new WireViewTestState(), edited);
        var restored = (await second.GetConfigurationAsync()).Configuration.Monitoring.ConnectorProfiles.Profiles[0];
        TestAssert.Equal("My connector preferences", restored.Name);
        TestAssert.False(restored.Metrics[0].AllowFallback, "Restarting must not overwrite the user's edited priorities.");
    }

    private static async Task ReloadsAnExternalAtomicFileReplacementAsync()
    {
        var device = new WireViewTestState();
        await using var host = await MonitoringTestHost.StartWithWireViewAsync(device);
        var current = await host.GetConfigurationAsync();
        current.Configuration.Plugins[PluginId] = new PluginSettings { Enabled = false, PollIntervalMs = 200 };
        var replacement = host.ConfigurationPath + ".replacement";

        await File.WriteAllTextAsync(replacement, RuntimeJson.Serialize(current.Configuration));
        File.Move(replacement, host.ConfigurationPath, overwrite: true);
        await TestWait.UntilAsync(async () => (await host.GetConfigurationAsync()).Revision > current.Revision, "file reload");

        var updated = await host.GetConfigurationAsync();
        TestAssert.False(updated.Configuration.Plugins[PluginId].Enabled, "File edits must apply to plugin lifecycle.");
        TestAssert.Equal(1, device.DisposeCount);
    }

    private static async Task RetainsValidSettingsWhenTheFileIsInvalidAsync()
    {
        await using var host = await MonitoringTestHost.StartWithoutPluginsAsync();
        var originalContents = await File.ReadAllTextAsync(host.ConfigurationPath);
        var initial = await host.GetConfigurationAsync();

        await File.WriteAllTextAsync(host.ConfigurationPath, "{ invalid JSON");
        await TestWait.UntilAsync(async () => (await host.GetConfigurationAsync()).LastError is not null, "invalid-file error");
        TestAssert.Equal(initial.Revision, (await host.GetConfigurationAsync()).Revision);

        await File.WriteAllTextAsync(host.ConfigurationPath, originalContents);
        await TestWait.UntilAsync(async () => (await host.GetConfigurationAsync()).LastError is null, "recovery after repairing the file");
        TestAssert.Equal("running", (await host.GetHealthAsync()).GetProperty("status").GetString());
    }

    private static async Task RejectsInvalidApiSettingsWithoutChangingTheFileAsync()
    {
        await using var host = await MonitoringTestHost.StartWithWireViewAsync(new WireViewTestState());
        var before = await File.ReadAllTextAsync(host.ConfigurationPath);
        var settings = new PluginSettings { PollIntervalMs = 0 };

        using var response = await host.Client.PutAsJsonAsync($"/plugins/{PluginId}/settings", settings);

        TestAssert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        TestAssert.Equal(before, await File.ReadAllTextAsync(host.ConfigurationPath));
    }

    private static async Task RetriesTemporaryLocksAndRejectsPersistentLocksAsync()
    {
        await using var host = await MonitoringTestHost.StartWithWireViewAsync(new WireViewTestState());
        var original = await host.GetConfigurationAsync();
        var settings = original.Configuration.Plugins[PluginId] with { Enabled = false };
        var directory = Path.GetDirectoryName(host.ConfigurationPath)!;
        var pattern = Path.GetFileName(host.ConfigurationPath) + ".*.tmp";

        using (HoldFile())
        using (var rejected = await Save().WaitAsync(TimeSpan.FromSeconds(5)))
        {
            TestAssert.Equal(HttpStatusCode.InternalServerError, rejected.StatusCode);
            TestAssert.Equal(original.Revision, (await host.GetConfigurationAsync()).Revision);
            TestAssert.False(Directory.EnumerateFiles(directory, pattern).Any(), "A rejected update must clean up its temporary file.");
        }

        Task<HttpResponseMessage> pending;
        using (HoldFile())
        {
            pending = Save();
            await TestWait.UntilAsync(() => Task.FromResult(Directory.EnumerateFiles(directory, pattern).Any()), "pending atomic replacement");
        }
        using var accepted = await pending;
        accepted.EnsureSuccessStatusCode();
        TestAssert.Equal(original.Revision + 1, (await host.GetConfigurationAsync()).Revision);
        TestAssert.False(RuntimeJson.Deserialize(await File.ReadAllTextAsync(host.ConfigurationPath)).Plugins[PluginId].Enabled,
            "A successful retry must persist the requested settings.");
        return;

        Task<HttpResponseMessage> Save() => host.Client.PutAsJsonAsync($"/plugins/{PluginId}/settings", settings);

        FileStream HoldFile() => File.Open(host.ConfigurationPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }

    private static async Task RollsBackAnActivationFailureAsync()
    {
        var device = new WireViewTestState();
        await using var host = await MonitoringTestHost.StartWithWireViewAsync(device);
        var disabled = new PluginSettings { Enabled = false };
        await host.SetPluginSettingsAsync(PluginId, disabled);
        device.FailCreation = true;

        using var response = await host.Client.PutAsJsonAsync($"/plugins/{PluginId}/settings", disabled with { Enabled = true });

        TestAssert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var persisted = RuntimeJson.Deserialize(await File.ReadAllTextAsync(host.ConfigurationPath));
        TestAssert.False(persisted.Plugins[PluginId].Enabled, "Failed activation must roll back persisted settings.");
        var effective = await host.GetConfigurationAsync();
        TestAssert.False(effective.Configuration.Plugins[PluginId].Enabled, "Failed activation must retain effective settings.");
        TestAssert.True(effective.LastError?.Contains("were restored") == true, "The rollback outcome must remain visible.");
    }

    private static async Task PartialRecoveryPreservesEachFailureAsync()
    {
        var apply = new InvalidOperationException("activation");
        var disk = new IOException("disk");
        var runtime = new InvalidOperationException("runtime");
        foreach (var diskFails in new[] { false, true })
        foreach (var runtimeFails in new[] { false, true })
        {
            var attempts = new List<string>();
            var failure = await RuntimeConfigurationService.RecoverAsync(apply, 7,
                () => { attempts.Add("disk"); return diskFails ? Task.FromException(disk) : Task.CompletedTask; },
                () => { attempts.Add("runtime"); return runtimeFails ? Task.FromException(runtime) : Task.CompletedTask; });
            TestAssert.True(attempts.SequenceEqual(["disk", "runtime"]), "Both recovery steps must run.");
            if (!diskFails && !runtimeFails)
            {
                TestAssert.True(failure is null, "Complete recovery preserves the original apply exception.");
                continue;
            }
            TestAssert.True(failure is not null && failure.InnerExceptions[0] == apply, "The original apply error must come first.");
            TestAssert.Equal(diskFails, failure!.InnerExceptions.Contains(disk));
            TestAssert.Equal(runtimeFails, failure.InnerExceptions.Contains(runtime));
            TestAssert.True(failure.Message.Contains("revision 7"), "Recovery must identify the attempted revision.");
        }
    }

    private static async Task ReleasesResourcesAcrossRepeatedEnableDisableCyclesAsync()
    {
        var device = new WireViewTestState();
        await using var host = await MonitoringTestHost.StartWithWireViewAsync(device);
        for (var cycle = 0; cycle < 3; cycle++)
        {
            await host.SetPluginSettingsAsync(PluginId, new PluginSettings { Enabled = false });
            await host.SetPluginSettingsAsync(PluginId, new PluginSettings { Enabled = true });
        }

        TestAssert.Equal(4, device.CreateCount);
        TestAssert.Equal(3, device.DisposeCount);
        TestAssert.Equal(0, device.ConcurrentAccessDetected);
    }

    private static async Task RejectsInvalidExternalSettingsWithoutStoppingThePluginAsync()
    {
        var device = new WireViewTestState();
        await using var host = await MonitoringTestHost.StartWithWireViewAsync(device);
        var current = await host.GetConfigurationAsync();
        current.Configuration.Plugins[PluginId] = new PluginSettings { PollIntervalMs = 0 };

        await File.WriteAllTextAsync(host.ConfigurationPath, RuntimeJson.Serialize(current.Configuration));
        await TestWait.UntilAsync(async () => (await host.GetConfigurationAsync()).LastError is not null, "invalid settings error");

        var effective = await host.GetConfigurationAsync();
        TestAssert.Equal(current.Revision, effective.Revision);
        TestAssert.Equal(1000, effective.Configuration.Plugins[PluginId].PollIntervalMs);
        TestAssert.Equal(0, device.DisposeCount);
    }

    private static async Task DoesNotWaitForUnchangedHangingPluginsAsync()
    {
        await using var host = await MonitoringTestHost.StartWithTestPluginsAsync();
        await host.GetSensorSnapshotAsync();
        var current = await host.GetConfigurationAsync();

        await host.SetConfigurationAsync(current.Configuration with { Monitoring = new MonitoringOptions { StreamIntervalMs = 200 } });

        TestAssert.Equal(200, (await host.GetConfigurationAsync()).Configuration.Monitoring.StreamIntervalMs);
    }
}
