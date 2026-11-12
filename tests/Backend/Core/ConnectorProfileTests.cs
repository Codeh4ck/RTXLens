using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Health;
using RTXLens.Contracts.Monitoring;
using RTXLens.Contracts.Sensors;
using RTXLens.Core;
using RTXLens.Core.Plugins;
using RTXLens.Plugins.Msi.Mapping;
using RTXLens.Tests.Infrastructure;

namespace RTXLens.Tests.Core;

internal static class ConnectorProfileTests
{
    private static readonly string[] Producers = ["nvidia", "astral", "wireview", "psu"];

    public static void Run(TestRunner runner)
    {
        runner.Run(DefaultsFollowPluginPrioritiesAndDedicatedProfilesDoNotFallBack);
        runner.Run(ResolvesEachMetricIndependentlyAndReturnsToThePreferredSource);
        runner.Run(RejectsDisabledStaleAmbiguousAndBoardSources);
        runner.Run(FallbackOffNeverUsesTheSecondSource);
        runner.Run(MsiRequiresCompletePinsAndHandlesIdleAmbiguityAndManualSelection);
    }

    private static ConnectorProfileResolver Resolver() => new([
        new Provider("nvidia", 0, 0, 100), new Provider("astral", 10, 10, 0),
        new Provider("wireview", 20, 20, 20, true), new Provider("psu", null, 30, null)]);

    private static ServiceConfiguration Configuration(ConnectorProfileResolver resolver) => new()
    {
        Plugins = Producers.ToDictionary(id => id, _ => new PluginSettings()),
        Monitoring = new MonitoringOptions { ConnectorProfiles = resolver.CreateDefaults() }
    };

    private static SensorSnapshot Snapshot(params SensorReading[] readings) => new(1, 1, DateTimeOffset.UtcNow, readings,
        [.. Producers.Select(id => new PluginHealth(id, PluginState.Ready, DateTimeOffset.UtcNow, null))], []);

    private static SensorReading Reading(string id, SensorMetric metric, double value = 12) => new(
        new SensorIdentity(id, "gpu0", SensorComponent.GpuConnector, metric, 1), SensorUnits.For(metric), value, DateTimeOffset.UtcNow);

    private static string? Source(SensorSnapshot snapshot, SensorMetric metric) => snapshot.Selections
        .Single(selection => selection.Name == ConnectorProfiles.SelectionName(metric)).SensorKey;

    private static void DefaultsFollowPluginPrioritiesAndDedicatedProfilesDoNotFallBack()
    {
        var defaults = Resolver().CreateDefaults();
        TestAssert.True(defaults.IsValid(), "Generated profiles must validate.");
        var automatic = defaults.Profiles.Single(profile => profile.Id == "automatic");
        TestAssert.Equal("nvidia,astral,wireview", string.Join(',', automatic.Metrics.Single(metric => metric.Metric == SensorMetric.Power).PreferredPlugins));
        TestAssert.Equal("nvidia,astral,wireview,psu", string.Join(',', automatic.Metrics.Single(metric => metric.Metric == SensorMetric.Current).PreferredPlugins));
        TestAssert.Equal("astral,wireview,nvidia", string.Join(',', automatic.Metrics.Single(metric => metric.Metric == SensorMetric.Voltage).PreferredPlugins));
        TestAssert.True(defaults.Profiles.Single(profile => profile.Id == "wireview").Metrics.All(metric => !metric.AllowFallback
            && metric.PreferredPlugins.SequenceEqual(["wireview"])), "Dedicated profiles must not silently fall back.");
    }

    private static void ResolvesEachMetricIndependentlyAndReturnsToThePreferredSource()
    {
        var resolver = Resolver();
        var configuration = Configuration(resolver);
        var power = Reading("nvidia", SensorMetric.Power, 300);
        var current = Reading("wireview", SensorMetric.Current, 25);
        var voltage = Reading("astral", SensorMetric.Voltage, 12);
        var frame = Snapshot(power, current, voltage);
        var resolved = resolver.Resolve(frame, configuration);
        TestAssert.Equal(power.Identity.Key, Source(resolved, SensorMetric.Power));
        TestAssert.Equal(current.Identity.Key, Source(resolved, SensorMetric.Current));
        TestAssert.Equal(voltage.Identity.Key, Source(resolved, SensorMetric.Voltage));

        var preferred = Reading("nvidia", SensorMetric.Current, 24.9);
        resolved = resolver.Resolve(frame with { Sensors = [.. frame.Sensors, preferred] }, configuration);
        TestAssert.Equal(preferred.Identity.Key, Source(resolved, SensorMetric.Current));
        TestAssert.Equal(4, resolved.Sensors.Length);
    }

    private static void RejectsDisabledStaleAmbiguousAndBoardSources()
    {
        var resolver = Resolver();
        var configuration = Configuration(resolver);
        var preferred = Reading("nvidia", SensorMetric.Power, 300);
        var fallback = Reading("wireview", SensorMetric.Power, 299);
        var frame = Snapshot(preferred with { ObservedAt = DateTimeOffset.UtcNow.AddSeconds(-30) }, fallback);
        TestAssert.Equal(fallback.Identity.Key, Source(resolver.Resolve(frame, configuration), SensorMetric.Power));
        configuration.Plugins["nvidia"] = new PluginSettings { Enabled = false };
        TestAssert.Equal(fallback.Identity.Key, Source(resolver.Resolve(Snapshot(preferred, fallback), configuration), SensorMetric.Power));

        configuration.Plugins["nvidia"] = new PluginSettings();
        var otherGpu = preferred with { Identity = preferred.Identity with { DeviceId = "gpu1" } };
        TestAssert.Equal(fallback.Identity.Key, Source(resolver.Resolve(Snapshot(preferred, otherGpu, fallback), configuration), SensorMetric.Power));
        var board = preferred with { Identity = preferred.Identity with { Component = SensorComponent.GpuBoard } };
        TestAssert.Equal(fallback.Identity.Key, Source(resolver.Resolve(Snapshot(board, fallback), configuration), SensorMetric.Power));
    }

    private static void FallbackOffNeverUsesTheSecondSource()
    {
        var resolver = Resolver();
        var configuration = Configuration(resolver);
        configuration.Monitoring.ConnectorProfiles.Profiles[0].Metrics.Single(metric => metric.Metric == SensorMetric.Power).AllowFallback = false;
        TestAssert.True(Source(resolver.Resolve(Snapshot(Reading("wireview", SensorMetric.Power)), configuration), SensorMetric.Power) is null,
            "Missing preferred readings must remain unavailable with fallback off.");
    }

    private static void MsiRequiresCompletePinsAndHandlesIdleAmbiguityAndManualSelection()
    {
        var selector = new MsiConnectorSelection("msi-psu");
        var now = DateTimeOffset.UtcNow;

        TestAssert.Equal(0, selector.Select(Frame(0, 0), null).Length);
        TestAssert.Equal("2", selector.Select(Frame(0, 1), null).Single().ConnectorId);
        TestAssert.Equal("2", selector.Select(Frame(0, 0), null).Single().ConnectorId);
        TestAssert.Equal(0, selector.Select(Frame(1, 1), null).Length);
        TestAssert.Equal("1", selector.Select(Frame(1, 1), "1").Single().ConnectorId);
        TestAssert.Equal(0, selector.Select(Frame(0, 0, "new-psu"), null).Length);
        var incomplete = Frame(0, 1);
        TestAssert.Equal(0, selector.Select(incomplete with { Sensors = [.. incomplete.Sensors.Skip(1)] }, null).Length);
        return;

        SensorSnapshot Frame(double first, double second, string device = "psu0")
        {
            var readings = new List<SensorReading>();
            foreach (var connector in new[] { "1", "2" })
            {
                var current = connector == "1" ? first : second;
                readings.AddRange(Enumerable.Range(1, 6).Select(pin => new SensorReading(new SensorIdentity("msi-psu", device,
                    SensorComponent.PsuPin, SensorMetric.Current, pin, connector), SensorUnit.Amperes, current, now)));
                readings.Add(new SensorReading(new SensorIdentity("msi-psu", device, SensorComponent.PsuConnector,
                    SensorMetric.Current, ConnectorId: connector), SensorUnit.Amperes, current * 6, now));
            }

            return new SensorSnapshot(1, 1, now, [.. readings], [new PluginHealth("msi-psu", PluginState.Ready, now, null)], []);
        }
    }

    private sealed class Provider(string id, int? power, int? current, int? voltage, bool dedicated = false) : IConnectorSourceProvider
    {
        public string Id => id;
        public bool IsDedicatedProfile => dedicated;
        public int? Priority(SensorMetric metric) => metric switch { SensorMetric.Power => power, SensorMetric.Current => current, SensorMetric.Voltage => voltage, _ => null };
        public SensorIdentity[] ConnectorSources(SensorSnapshot snapshot, PluginSettings settings, string? connectorId) =>
            [.. snapshot.Sensors.Where(reading => reading.Identity.PluginId == Id).Select(reading => reading.Identity)];
    }
}
