using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using RTXLens.Client.Http;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Health;
using RTXLens.Contracts.Sensors;
using RTXLens.Contracts.Warnings;
using RTXLens.Service.Configuration;
using RTXLens.Service.Services;
using RTXLens.Tests.Fixtures;
using RTXLens.Tests.Infrastructure;
using RTXLens.Tests.TestDoubles;
using RTXLens.Warnings;

namespace RTXLens.Tests.Service;

internal static class DeviceWarningProfileTests
{
    private static readonly DeviceWarningProfile Astral = new();
    private static readonly DeviceWarningProfile Msi = new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = "GPU cable",
        PluginId = "msi-psu",
        ConnectorId = "2"
    };

    public static async Task RunAsync(TestRunner runner)
    {
        runner.Run(ProfilesUseIndependentSourcesAndReminderPolicies);
        runner.Run(Uses5080ActivationAndRequiresFreshSustainedSamples);
        runner.Run(ShutdownRequiresContinuousWarningAndSurvivesReboot);
        await runner.RunAsync(PersistsProfilesThroughLocalDesktopApiAsync);
    }

    private static void ProfilesUseIndependentSourcesAndReminderPolicies()
    {
        var evaluator = new DeviceWarningEvaluator();
        var warnings = new WarningEngine(WarningTestData.Registry());
        var configuration = Configuration();
        var wireView = new DeviceWarningProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Inline monitor",
            PluginId = "wireview-pro2",
            NotificationPolicy = new WarningPolicy { RepeatWhileActive = true }
        };
        var profiles = new[] { Astral, Msi, wireView };

        var first = Evaluate(0);
        TestAssert.Equal(1, first.States.Length);
        TestAssert.Equal(null, first.States.Single().ProfileId);
        var active = Evaluate(3);
        TestAssert.Equal(4, active.States.Count(state => state.Phase == WarningPhase.Active));
        TestAssert.True(active.States.Any(state => state.ProfileId == Astral.Id && state.Title.StartsWith("ROG Astral")), "Astral warning source");
        TestAssert.True(active.States.Any(state => state.ProfileId == Msi.Id && state.Title.StartsWith("MSI PSU connector 2")), "PSU connector warning source");
        TestAssert.True(active.States.Any(state => state.ProfileId == wireView.Id && state.Title.StartsWith("WireView Pro II")), "WireView warning source");

        var recovered = Evaluate(5, psu: 1);
        TestAssert.Equal(WarningPhase.Recovering, recovered.States.Single(state => state.ProfileId == Msi.Id).Phase);
        var reminder = Evaluate(8, psu: 1);
        TestAssert.False(reminder.States.Any(state => state.ProfileId == Msi.Id), "The other PSU connector must not keep the profile active.");
        TestAssert.Equal(wireView.Id, reminder.States.Single(state => state.NotificationSequence == reminder.Sequence).ProfileId);
        return;

        WarningSnapshot Evaluate(int seconds, double astral = 6.5, double psu = 6.4, double wire = 6.2)
        {
            var snapshot = Frame(seconds, astral, psu, wire);
            var result = evaluator.Evaluate(snapshot, configuration, profiles, SettingsProfile.Rtx5080);

            TestAssert.Equal(2, result.Snapshot.PinWarningLimits!.Length);
            TestAssert.Equal(6d, result.Snapshot.PinWarningLimits[0].OvercurrentAmperes);
            return warnings.Evaluate(result.Snapshot, configuration, snapshot.Timestamp, result.Warnings,
                observation => observation.PluginId != "asus-astral" && observation.TypeId != ActiveSettingsProfile.WireViewProfileOvercurrent);
        }
    }

    private static void Uses5080ActivationAndRequiresFreshSustainedSamples()
    {
        var evaluator = new DeviceWarningEvaluator();
        var configuration = Configuration();
        var start = Frame(0, 5, 0, 0, lowAstral: 4);

        TestAssert.False(Evaluate(start).Warnings.Any(item => item.Observation.Active), "The initial sample must not trigger a warning.");
        TestAssert.False(Evaluate(start with { Timestamp = start.Timestamp.AddSeconds(3) }).Warnings.Any(item => item.Observation.Active),
            "A repeated snapshot is not a fresh sample.");
        var active = Evaluate(Frame(3, 5, 0, 0, lowAstral: 4)).Warnings.Single(item => item.Observation.Active);
        TestAssert.Equal("asus-astral/monitor-imbalance", active.Observation.TypeId);

        var stale = Evaluate(Frame(3, 5, 0, 0, lowAstral: 4) with { Timestamp = start.Timestamp.AddSeconds(30) });
        TestAssert.False(stale.Warnings.Any(item => item.Observation.Available), "Stale samples must not sustain warnings.");
        return;

        DeviceWarningEvaluation Evaluate(SensorSnapshot snapshot) => evaluator.Evaluate(snapshot, configuration, [Astral], SettingsProfile.Rtx5080);
    }

    private static void ShutdownRequiresContinuousWarningAndSurvivesReboot()
    {
        var path = Path.Combine(Path.GetTempPath(), "rtx-shutdown-test-" + Guid.NewGuid().ToString("N"));
        var configurationFile = new RuntimeConfigurationFile(Path.Combine(path, "appsettings.json"));
        var reasonPath = Path.Combine(path, "last-warning-shutdown.json");
        var clock = new TestTimeProvider();
        var action = new FakeShutdown(reasonPath);
        var coordinator = new WarningShutdownCoordinator(configurationFile, clock, action, NullLogger<WarningShutdownCoordinator>.Instance);
        var evaluator = new DeviceWarningEvaluator();
        var configuration = Configuration();
        var profile = Msi with { ShutdownAfterSeconds = 2 };

        try
        {
            Evaluate(0, 7);
            Evaluate(3, 7);
            Evaluate(5, 7, useNewSample: false);
            TestAssert.Equal(0, action.Count);

            Evaluate(5, 1);
            Evaluate(6, 7);
            Evaluate(9, 7);
            Evaluate(10, 7);
            TestAssert.Equal(0, action.Count);
            Evaluate(11, 7);
            Evaluate(12, 7);
            TestAssert.Equal(1, action.Count);
            TestAssert.Equal(null, coordinator.ReadAfterBoot());

            clock.Advance(TimeSpan.FromMinutes(1));
            var restarted = new WarningShutdownCoordinator(configurationFile, clock, action, NullLogger<WarningShutdownCoordinator>.Instance);
            var reason = restarted.ReadAfterBoot() ?? throw new InvalidOperationException("The next boot should see the reason.");
            TestAssert.Equal("MSI PSU connector 2", reason.Source);
            TestAssert.Equal(profile.Name, reason.ProfileName);
            restarted.Acknowledge("different-id");
            TestAssert.True(File.Exists(reasonPath), "A different id must not clear the reason.");
            restarted.Acknowledge(reason.Id);
            TestAssert.Equal(null, restarted.ReadAfterBoot());
        }
        finally
        {
            File.Delete(reasonPath);
            if (Directory.Exists(path))
            {
                Directory.Delete(path);
            }
        }

        return;

        void Evaluate(int seconds, double current, bool useNewSample = true)
        {
            var frame = Frame(seconds, 0, current, 0);
            if (!useNewSample)
            {
                frame = frame with { Sensors = [.. frame.Sensors.Select(sensor => sensor with { ObservedAt = frame.Timestamp.AddSeconds(-2) })] };
            }

            var result = evaluator.Evaluate(frame, configuration, [profile], SettingsProfile.Rtx5080);
            coordinator.Evaluate(result.Shutdowns);
        }
    }

    private static async Task PersistsProfilesThroughLocalDesktopApiAsync()
    {
        await using var host = await MonitoringTestHost.StartWithInstalledPluginsDisabledAsync();
        var api = new MonitoringApiClient(host.Client);
        var token = CancellationToken.None;
        var catalog = await api.GetDeviceWarningProfilesAsync(token);
        TestAssert.Equal(DeviceWarningProfile.DefaultId, catalog.Profiles.Single().Id);

        await api.SaveDeviceWarningProfileAsync(Msi, token);
        var saved = await api.GetDeviceWarningProfilesAsync(token);
        TestAssert.Equal(Msi, saved.Profiles.Single(profile => profile.Id == Msi.Id));
        TestAssert.Equal(2, saved.Profiles.Count(profile => profile.Enabled));

        using var crossOrigin = new HttpRequestMessage(HttpMethod.Delete, $"warning-profiles/{Msi.Id}");
        crossOrigin.Headers.Add("Origin", "https://example.com");
        using var forbidden = await host.Client.SendAsync(crossOrigin, token);
        TestAssert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        await api.DeleteDeviceWarningProfileAsync(Msi.Id, token);
        TestAssert.Equal(1, (await api.GetDeviceWarningProfilesAsync(token)).Profiles.Length);
        TestAssert.Equal(null, await api.GetWarningShutdownAsync(token));
    }

    private static ServiceConfiguration Configuration()
    {
        var configuration = WarningTestData.Configuration();
        configuration.Plugins["msi-psu"] = new PluginSettings();
        return configuration;
    }

    private static SensorSnapshot Frame(int seconds, double astral, double msi, double wire, double? lowAstral = null)
    {
        var now = WarningTestData.Start.AddSeconds(seconds);
        var fixture = WarningTestData.Frame(seconds, temperature: 70, faults: 8);
        return fixture with
        {
            Plugins = [.. fixture.Plugins, new PluginHealth("msi-psu", PluginState.Ready, now, null)],
            Sensors =
            [
                .. fixture.Sensors.Select(reading => reading.Identity is { PluginId: "asus-astral", Metric: SensorMetric.Current }
                    ? reading with { Value = reading.Identity.Channel == 1 ? astral : lowAstral ?? astral } : reading),
                .. Pins("msi-psu", "psu", SensorComponent.PsuPin, "1", 8),
                .. Pins("msi-psu", "psu", SensorComponent.PsuPin, "2", msi),
                .. Pins("wireview-pro2", "com3", SensorComponent.GpuPin, null, wire)
            ]
        };

        IEnumerable<SensorReading> Pins(string plugin, string device, SensorComponent component, string? connector, double value) =>
            Enumerable.Range(1, 6).Select(pin => new SensorReading(new SensorIdentity(plugin, device, component, SensorMetric.Current, pin, connector),
                SensorUnit.Amperes, value, now));
    }

    private sealed class FakeShutdown(string reasonPath) : IWarningShutdown
    {
        public int Count { get; private set; }

        public void Request()
        {
            TestAssert.True(File.Exists(reasonPath), "The reason must be saved before requesting Windows shutdown.");
            Count++;
        }
    }
}
