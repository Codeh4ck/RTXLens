using RTXLens.Contracts.Configuration;
using RTXLens.Core.Plugins;
using RTXLens.Tests.Infrastructure;
using RTXLens.Tests.TestDoubles;

namespace RTXLens.Tests.Service;

internal static class PluginIsolationTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("Healthy devices stream while another plugin initializes", () => StreamsWhileDeviceIsBlockedAsync(blockFirstRead: false));
        await runner.RunAsync("Healthy devices stream during a blocked synchronous hardware read", () => StreamsWhileDeviceIsBlockedAsync(blockFirstRead: true));
        await runner.RunAsync(StreamsWireViewWhileGpuProfileDiscoveryIsBlockedAsync);
        await runner.RunAsync(RetriesFailedStartupWithoutRestartingTheServiceAsync);
        await runner.RunAsync(DiscoversReturningHardwareWhileAnotherProbeIsBlockedAsync);
    }

    private static async Task StreamsWhileDeviceIsBlockedAsync(bool blockFirstRead)
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = blockFirstRead ? new Factory("slow", beforePoll: WaitForDevice) : new Factory("slow", initialize: WaitForDevice);
        await using var host = await MonitoringTestHost.StartWithFactoriesAsync(slow, new Factory("healthy"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var socket = await host.ConnectSensorClientAsync(timeout.Token);
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            await host.GetHealthAsync();
            await host.Client.GetStringAsync("/plugins", timeout.Token);
            var configuration = await host.GetConfigurationAsync();
            await host.SetConfigurationAsync(configuration.Configuration with { Monitoring = new() { StreamIntervalMs = 200 } });

            var first = await WebSocketMessageReader.ReadUntilAsync(socket, frame => HasState(frame, "healthy", "Ready")
                && HasState(frame, "slow", "Starting"), timeout.Token);
            var firstReading = first.GetProperty("sensors")[0].GetProperty("observedAt").GetDateTimeOffset();
            await WebSocketMessageReader.ReadUntilAsync(socket, frame => HasState(frame, "slow", "Starting")
                && frame.GetProperty("sensors")[0].GetProperty("observedAt").GetDateTimeOffset() > firstReading, timeout.Token);
        }
        finally
        {
            release.Set();
        }

        await WebSocketMessageReader.ReadUntilAsync(socket, frame => HasState(frame, "slow", "Ready"), timeout.Token);
        return;

        void WaitForDevice()
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(15)))
            {
                throw new TimeoutException("Device remained blocked.");
            }
        }
    }

    private static async Task StreamsWireViewWhileGpuProfileDiscoveryIsBlockedAsync()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gpu = new TestNvidiaHardwareSource
        {
            Devices = [new("gpu0", "gpu0", "RTX 5080")],
            BeforeDiscovery = () =>
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(15)))
                {
                    throw new TimeoutException("GPU discovery remained blocked.");
                }
            }
        };
        await using var host = await MonitoringTestHost.StartWithWireViewAsync(new WireViewTestState(), 100, gpu);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var socket = await host.ConnectSensorClientAsync(timeout.Token);
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            var first = await WebSocketMessageReader.ReadUntilAsync(socket, frame => HasState(frame, "wireview-pro2", "Ready"), timeout.Token);
            var observed = first.GetProperty("sensors")[0].GetProperty("observedAt").GetDateTimeOffset();
            await WebSocketMessageReader.ReadUntilAsync(socket, frame => HasState(frame, "wireview-pro2", "Ready")
                && frame.GetProperty("sensors")[0].GetProperty("observedAt").GetDateTimeOffset() > observed, timeout.Token);
        }
        finally
        {
            release.Set();
        }

        await WebSocketMessageReader.ReadUntilAsync(socket, frame => frame.TryGetProperty("settingsProfile", out var profile)
            && profile.ValueKind == System.Text.Json.JsonValueKind.Object
            && profile.GetProperty("profile").GetProperty("id").GetString() == "rtx-5080", timeout.Token);
    }

    private static async Task RetriesFailedStartupWithoutRestartingTheServiceAsync()
    {
        var attempts = 0;
        var device = new Factory("recovering", initialize: () =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new IOException("Driver is still starting.");
            }
        });

        await using var host = await MonitoringTestHost.StartWithFactoriesAsync(device);
        var snapshot = await host.GetSensorSnapshotAsync();
        TestAssert.True(HasState(snapshot, "recovering", "Ready"), "Device should recover after a failed initialization.");
        TestAssert.Equal(2, Volatile.Read(ref attempts));
    }

    private static async Task DiscoversReturningHardwareWhileAnotherProbeIsBlockedAsync()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stalled = new Factory("stalled", probe: () =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(15)))
            {
                throw new TimeoutException("Probe remained blocked.");
            }

            return true;
        });
        var returning = new Factory("returning", probe: () => true);
        await using var host = await MonitoringTestHost.StartWithFactoriesAsync(stalled, returning);
        await host.SetPluginSettingsAsync("returning", new PluginSettings { Enabled = false, AutomaticallyDisabled = true, PollIntervalMs = 100 });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var socket = await host.ConnectSensorClientAsync(timeout.Token);
            await WebSocketMessageReader.ReadUntilAsync(socket, frame => HasState(frame, "returning", "Ready"), timeout.Token);
        }
        finally
        {
            release.Set();
        }
    }

    private static bool HasState(System.Text.Json.JsonElement frame, string id, string state) =>
        frame.GetProperty("plugins").EnumerateArray().Any(plugin => plugin.GetProperty("pluginId").GetString() == id
            && plugin.GetProperty("state").GetString() == state);

    private sealed class Factory(string id, Action? initialize = null, Func<bool?>? probe = null, Action? beforePoll = null) : ISensorPluginFactory
    {
        public string Id => id;
        public string Name => id;
        public PluginSettings CreateDefaultSettings() => new() { PollIntervalMs = 100 };
        public bool? HardwarePresent(PluginSettings settings) => probe?.Invoke();

        public ISensorPlugin Create(PluginSettings settings)
        {
            initialize?.Invoke();
            return new TestSensorPlugin(id, beforePoll: beforePoll);
        }
    }
}
