using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Health;
using RTXLens.Contracts.Sensors;
using RTXLens.Core.Configuration;
using RTXLens.Hardware.Nvidia;

using RTXLens.Service.Configuration;

using RTXLens.Tests.Fixtures;
using RTXLens.Tests.Infrastructure;
using RTXLens.Tests.TestDoubles;


namespace RTXLens.Tests.Service;

internal static class SettingsProfileTests
{
    public static void Run(TestRunner runner)
    {
        runner.Run(Detects5080ForWireViewWithoutAstralTelemetry);
        runner.Run(UsesLegacy5090ThresholdAndPreservesCustomAutoSettings);

        runner.Run(SavesNamedProfilesAndRestoresThemAfterRestart);
    }

    public static async Task RunEndpointsAsync(TestRunner runner)
    {
        await runner.RunAsync(LoadsAndAppliesSettingsProfilesThroughTheDesktopClientAsync);
    }

    private static async Task LoadsAndAppliesSettingsProfilesThroughTheDesktopClientAsync()
    {
        await using var host = await MonitoringTestHost.StartWithInstalledPluginsDisabledAsync();
        var api = new Client.Http.MonitoringApiClient(host.Client);
        var catalog = await api.GetSettingsProfilesAsync(CancellationToken.None);
        TestAssert.Equal("auto", catalog.SelectedId);
        var preset = catalog.Profiles.Single(profile => profile.Id == "rtx-5080");
        TestAssert.Equal(6d, preset.OvercurrentAmperes);
        var applied = await api.ApplySettingsProfileAsync(
            new SettingsProfileRequest(preset.Id, preset with { CurrentCeiling = 7 }, false), CancellationToken.None);
        TestAssert.Equal<double?>(7, applied.Effective?.CurrentCeiling);
        var created = await api.ApplySettingsProfileAsync(
            new SettingsProfileRequest(preset.Id, preset with { Name = "My chart" }, true), CancellationToken.None);
        var custom = created.Profiles.Single(profile => profile.Id == created.SelectedId);
        await api.ApplySettingsProfileAsync(new SettingsProfileRequest("auto", null, false), CancellationToken.None);

        var updated = await api.UpdateSettingsProfileAsync(custom with { CurrentCeiling = 9 }, CancellationToken.None);
        TestAssert.Equal("auto", updated.SelectedId);
        TestAssert.Equal(9d, updated.Profiles.Single(profile => profile.Id == custom.Id).CurrentCeiling);
        TestAssert.Equal(created.Profiles.Length, updated.Profiles.Length);

        await api.ApplySettingsProfileAsync(new SettingsProfileRequest(custom.Id, null, false), CancellationToken.None);
        var deleted = await api.DeleteSettingsProfileAsync(custom.Id, CancellationToken.None);
        TestAssert.Equal("auto", deleted.SelectedId);
        TestAssert.False(deleted.Profiles.Any(profile => profile.Id == custom.Id), "Deleting a chart profile should remove it from the catalog.");
    }

    private static void Detects5080ForWireViewWithoutAstralTelemetry()
    {
        var snapshot = Frame("wireview-pro2", "com3", 0, 5, 5);
        var profile = Resolve("NVIDIA GeForce RTX 5080", snapshot);

        TestAssert.Equal("rtx-5080", profile.Profile.Id);
        TestAssert.Equal("com3", profile.WireViewDeviceId);
        TestAssert.Equal(6d, profile.Profile.CurrentCeiling);
        TestAssert.Equal(75d, profile.Profile.ResolvePowerCeiling(600));
    }

    private static void UsesLegacy5090ThresholdAndPreservesCustomAutoSettings()
    {
        var snapshot = Frame("asus-astral", "gpu0", 0, 8, 8);
        var active = Resolve("NVIDIA GeForce RTX 5090", snapshot);
        TestAssert.Equal(9.2, active.Profile.OvercurrentAmperes);
        TestAssert.Equal(10d, active.Profile.CurrentCeiling);

        var configuration = WarningTestData.Configuration();
        var previous = configuration.Plugins["asus-astral"];
        configuration.Plugins["asus-astral"] = Plugins.Asus.Configuration.AsusSettings.WithOptions(previous,
            new RTXLens.Plugins.Asus.Contracts.Configuration.AsusOptions { WireOvercurrentAmperageThreshold = 8 });
        TestAssert.Equal(8d, Resolve("NVIDIA GeForce RTX 5080", snapshot, configuration).Profile.OvercurrentAmperes);
    }

    private static void SavesNamedProfilesAndRestoresThemAfterRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), "rtxlens-profile-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = StoreConfiguration(path);
            var store = new SettingsProfileStore(options, NullLogger<SettingsProfileStore>.Instance);
            var custom = SettingsProfile.Rtx5080 with { Name = "My Astral", CurrentCeiling = 7 };
            store.Apply(new SettingsProfileRequest("rtx-5080", custom, false));
            TestAssert.False(Directory.Exists(path), "Applying session edits must not create profile files.");
            store.Apply(new SettingsProfileRequest("rtx-5080", custom, true));
            var id = store.Selection().Id;
            store.Update(id, custom with { CurrentCeiling = 8 });

            var reopened = new SettingsProfileStore(options, NullLogger<SettingsProfileStore>.Instance);
            var restored = reopened.Selection().Profile;
            TestAssert.Equal(id, restored?.Id);
            TestAssert.Equal("My Astral", restored?.Name);
            TestAssert.Equal<double?>(8, restored?.CurrentCeiling);
            TestAssert.Equal(1, Directory.GetFiles(path, "*.profile.json").Length);

            reopened.Delete(id);
            var afterDelete = new SettingsProfileStore(options, NullLogger<SettingsProfileStore>.Instance);
            TestAssert.Equal("auto", afterDelete.Selection().Id);
            TestAssert.Equal(0, Directory.GetFiles(path, "*.profile.json").Length);
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private static ActiveSettingsProfile Resolve(string name, SensorSnapshot snapshot, ServiceConfiguration? configuration = null)
    {
        var hardware = new TestNvidiaHardwareSource { Devices = [new("gpu0", "/gpu-nvidia/0", name)] };
        using var services = new ServiceCollection()
            .AddSingleton(_ => new NvidiaTelemetryReader(hardware, TimeProvider.System)).BuildServiceProvider();
        var store = new SettingsProfileStore(StoreConfiguration(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))),
            NullLogger<SettingsProfileStore>.Instance);
        var resolver = new SettingsProfileResolver(store, new ActiveSettingsProfileState(), services, NullLogger<SettingsProfileResolver>.Instance);
        return resolver.Resolve(snapshot, configuration ?? WarningTestData.Configuration())
            ?? throw new InvalidOperationException("The configured GPU was not recognized.");
    }

    private static IConfiguration StoreConfiguration(string path) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["SettingsProfiles:Directory"] = path }).Build();

    private static SensorSnapshot Frame(string pluginId, string deviceId, int seconds, double high, double low)
    {
        var now = WarningTestData.Start.AddSeconds(seconds);
        return new SensorSnapshot(1, seconds + 1, now,
            [.. Enumerable.Range(1, 6).Select(pin => new SensorReading(new SensorIdentity(pluginId, deviceId,
                SensorComponent.GpuPin, SensorMetric.Current, pin), SensorUnit.Amperes, pin == 1 ? high : low, now))],
            [new PluginHealth(pluginId, PluginState.Ready, now, null)], [])
        {
            SettingsProfile = new(SettingsProfile.Rtx5080, "gpu0", pluginId == "wireview-pro2" ? deviceId : null)
        };
    }
}
