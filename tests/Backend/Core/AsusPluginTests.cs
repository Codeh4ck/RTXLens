using RTXLens.Plugins.Asus.Contracts.Warnings;
using RTXLens.Plugins.Asus.Contracts.Configuration;
using System.Text.Json;
using RTXLens.Hardware.Nvidia;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Configuration;
using RTXLens.Plugins.Asus;
using RTXLens.Plugins.Asus.Configuration;
using RTXLens.Plugins.Asus.Device;
using RTXLens.Plugins.Asus.Streaming;
using RTXLens.Tests.Fixtures;
using RTXLens.Tests.Infrastructure;
using RTXLens.Tests.TestDoubles;

namespace RTXLens.Tests.Core;

internal static class AsusPluginTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        runner.Run(IdentifiesSupportedBoards);
        await runner.RunAsync(RequiresFreshShuntEvidenceForAlternativeInputsAsync);
        runner.Run(ComparesPluginOptionsByValue);
        runner.Run(ValidatesTheOriginalPersistedThresholdBounds);
        await runner.RunAsync(MapsEveryOriginalSensorWithoutMixingGpusAsync);
        await runner.RunAsync(PublishesMissingValuesWithUnavailableQualityAsync);
        await runner.RunAsync(PublishesCorrectedAlertsInBothFormatsAsync);
        await runner.RunAsync(ClearsCachedReadingsAndTimersAfterFailuresAsync);
        await runner.RunAsync(AppliesThresholdsToTheNextSampleAsync);
    }

    private static void IdentifiesSupportedBoards()
    {
        TestAssert.True(AstralBoard.IsSupported(0x89E31043), "5090 OC should match.");
        TestAssert.True(AstralBoard.IsSupported(0x89DF1043), "5080 non-OC should match.");
        TestAssert.False(AstralBoard.IsSupported(0x89E310DE), "Wrong board vendor must not match.");
    }

    private static async Task RequiresFreshShuntEvidenceForAlternativeInputsAsync()
    {
        var clock = new TestTimeProvider();
        var state = new AsusTestState();
        using var stream = new AsusStream();
        using var plugin = new AsusPlugin(new PluginSettings(), clock, new TestAsusReader(state), stream);
        var active = AsusTestData.Create() with { AlternativeInput = true };
        var noPins = active with { Sensors = [.. active.Sensors.Where(sensor =>
            sensor.Definition.Component is not (SensorComponent.GpuPin or SensorComponent.GpuConnector))] };

        TestAssert.Equal("Power path undetermined", await Observe(active));
        TestAssert.Equal("16-pin cable active", await Observe(active));
        TestAssert.Equal("16-pin cable active", await Observe(active, 0));
        TestAssert.Equal("Power path undetermined", await Observe(noPins));
        TestAssert.Equal("Power path undetermined", await Observe(noPins));
        var low = noPins with { CablePinsBelowThreshold = true };
        TestAssert.Equal("Power path undetermined", await Observe(low));
        TestAssert.Equal("Cable load not observed", await Observe(low));
        TestAssert.Equal("Power path undetermined", await Observe(low, 60));
        return;

        async Task<string?> Observe(AsusGpuData sample, int seconds = 1)
        {
            clock.Advance(TimeSpan.FromSeconds(seconds));
            state.Data = [sample with { ObservedAt = clock.GetUtcNow() }];
            return (await plugin.PollAsync(CancellationToken.None)).Message;
        }
    }

    private static void ComparesPluginOptionsByValue()
    {
        var first = new PluginSettings { Options = JsonSerializer.SerializeToElement(new { threshold = 7, deviceId = "gpu0" }) };
        var reordered = first with { Options = JsonSerializer.SerializeToElement(new { deviceId = "gpu0", threshold = 7 }) };
        var changed = first with { Options = JsonSerializer.SerializeToElement(new { threshold = 8, deviceId = "gpu0" }) };

        TestAssert.True(first.HasSameValues(reordered), "Equivalent JSON options must not reconfigure unchanged plugins.");
        TestAssert.False(first.HasSameValues(changed), "Changed provider options must be applied.");
    }

    private static void ValidatesTheOriginalPersistedThresholdBounds()
    {
        TestAssert.Throws<ArgumentException>(() => AsusSettings.WithOptions(new PluginSettings(), new AsusOptions { ImbalancePercentageThreshold = 51 }));
        var slow = AsusSettings.GetOptions(new PluginSettings { PollIntervalMs = 5000 });
        TestAssert.Equal(3000, slow.WireOvercurrentThresholdMs);

        var tooLong = new PluginSettings { Options = JsonSerializer.SerializeToElement(new { wireOvercurrentThresholdMs = 30001 }) };
        TestAssert.Throws<ArgumentException>(() => AsusSettings.GetOptions(tooLong));
    }

    private static async Task MapsEveryOriginalSensorWithoutMixingGpusAsync()
    {
        var state = new AsusTestState();
        using var stream = new AsusStream();
        using var plugin = new AsusPlugin(new PluginSettings(), TimeProvider.System, new TestAsusReader(state), stream);
        var sample = await plugin.PollAsync(CancellationToken.None);

        TestAssert.Equal(32, plugin.Latest!.Sensors.Count);
        TestAssert.Equal(41, sample.Sensors.Length);
        TestAssert.Equal(41, sample.Sensors.Select(sensor => sensor.Identity.Key).Distinct().Count());
        TestAssert.True(sample.Sensors.All(sensor => sensor.Identity.DeviceId == AsusTestData.FirstDeviceId), "Only one GPU may contribute readings.");
        var boardPower = sample.Sensors.Single(sensor => sensor.Identity.Component == SensorComponent.GpuBoard);
        var connectorPower = sample.Sensors.Single(sensor => sensor.Identity is { Component: SensorComponent.GpuConnector, Metric: SensorMetric.Power });
        TestAssert.Equal<double?>(470, boardPower.Value);
        TestAssert.Equal<double?>(450, connectorPower.Value);

        plugin.ApplySettings(AsusSettings.WithOptions(new PluginSettings(), new AsusOptions { DeviceId = AsusTestData.SecondDeviceId }));
        var selected = await plugin.PollAsync(CancellationToken.None);
        TestAssert.True(selected.Sensors.All(sensor => sensor.Identity.DeviceId == AsusTestData.SecondDeviceId), "Selection must change every sensor identity.");
        TestAssert.Equal(0, state.DisposeCount);
    }

    private static async Task PublishesMissingValuesWithUnavailableQualityAsync()
    {
        var data = AsusTestData.Create();
        var state = new AsusTestState
        {
            Data = [data with { Sensors = [.. data.Sensors.Select(sensor => sensor with { Value = (float?)null })] }]
        };
        using var stream = new AsusStream();
        using var plugin = new AsusPlugin(new PluginSettings(), TimeProvider.System, new TestAsusReader(state), stream);
        var sample = await plugin.PollAsync(CancellationToken.None);

        TestAssert.Equal(0, plugin.Latest!.Sensors.Count);
        TestAssert.True(sample.Sensors.All(sensor => sensor.Value is null && sensor.Quality == ReadingQuality.Unavailable),
            "Missing values and alert flags must remain unavailable rather than becoming false zero readings.");
    }

    private static async Task PublishesCorrectedAlertsInBothFormatsAsync()
    {
        var data = AsusTestData.Create();
        var sensors = data.Sensors.Select(sensor => sensor.Definition is { Component: SensorComponent.GpuPin, Metric: SensorMetric.Current } ? sensor with { Value = sensor.Definition.Channel == 1 ? 10 : 4 } : sensor).ToArray();
        var state = new AsusTestState { Data = [data with { Sensors = sensors }] };
        var clock = new TestTimeProvider();
        using var stream = new AsusStream();
        using var plugin = new AsusPlugin(new PluginSettings(), clock, new TestAsusReader(state), stream);

        await plugin.PollAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(3));
        var sample = await plugin.PollAsync(CancellationToken.None);

        TestAssert.True(plugin.Latest!.CurrentImbalance is not null, "Legacy imbalance details must be retained.");
        TestAssert.Equal("Pin 1", plugin.Latest.WireOvercurrent!.PinValues.Keys.Single());
        var overcurrent = sample.Sensors.Single(sensor => sensor.Identity is { Metric: SensorMetric.OvercurrentDetected, Channel: 1 });
        TestAssert.Equal<double?>(1, overcurrent.Value);
        TestAssert.Equal(SensorUnit.Boolean, overcurrent.Unit);
        var detail = sample.Alerts!.Single(alert => alert.Kind == AsusAlertKinds.WireOvercurrent);
        TestAssert.Equal(AsusTestData.FirstDeviceId, detail.DeviceId);
        TestAssert.Equal(1, detail.Pins.Single().Channel);
        TestAssert.Equal(10d, detail.Pins.Single().CurrentA);
        TestAssert.Equal(clock.GetUtcNow(), detail.ReportedAt);
    }

    private static async Task ClearsCachedReadingsAndTimersAfterFailuresAsync()
    {
        var state = new AsusTestState();
        using var stream = new AsusStream();
        using var plugin = new AsusPlugin(new PluginSettings(), TimeProvider.System, new TestAsusReader(state), stream);
        await plugin.PollAsync(CancellationToken.None);
        state.FailReads = true;

        try
        {
            await plugin.PollAsync(CancellationToken.None);
            throw new InvalidOperationException("The reader failure should propagate to plugin health.");
        }
        catch (IOException)
        {
            TestAssert.True(plugin.Latest is null, "A failed read must clear the legacy snapshot.");
        }

        state.FailReads = false;
        state.Connected = false;
        var sample = await plugin.PollAsync(CancellationToken.None);
        TestAssert.Equal(PluginState.NoDevice, sample.State);
        TestAssert.Equal(0, sample.Sensors.Length);
    }

    private static async Task AppliesThresholdsToTheNextSampleAsync()
    {
        var data = AsusTestData.Create();
        var sensors = data.Sensors.Select(sensor => sensor.Definition is { Component: SensorComponent.GpuPin, Metric: SensorMetric.Current } ? sensor with { Value = 10 } : sensor).ToArray();
        var state = new AsusTestState { Data = [data with { Sensors = sensors }] };
        var clock = new TestTimeProvider();
        using var stream = new AsusStream();
        using var plugin = new AsusPlugin(new PluginSettings(), clock, new TestAsusReader(state), stream);
        await plugin.PollAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(3));
        await plugin.PollAsync(CancellationToken.None);
        TestAssert.True(plugin.Latest!.WireOvercurrent is not null, "The original threshold should report overcurrent.");

        var updated = AsusSettings.WithOptions(new PluginSettings(), new AsusOptions { WireOvercurrentAmperageThreshold = 10.5f });
        plugin.ApplySettings(updated);
        var sample = await plugin.PollAsync(CancellationToken.None);

        TestAssert.True(plugin.Latest!.WireOvercurrent is null, "The next sample must evaluate the updated threshold.");
        TestAssert.True(sample.Sensors.Where(sensor => sensor.Identity.Metric == SensorMetric.OvercurrentDetected)
            .All(sensor => sensor.Value == 0), "Typed alert flags must also use the updated threshold.");
        TestAssert.Equal(0, state.DisposeCount);
    }
}
