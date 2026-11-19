using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Core.Plugins;
using RTXLens.Tests.Fixtures;
using RTXLens.Tests.Infrastructure;

namespace RTXLens.Tests.Core;

internal static class SensorStateStoreTests
{
    public static void Run(TestRunner runner)
    {
        runner.Run(ReusesUnchangedReadingsAcrossSnapshots);
        runner.Run(NoSelectionSnapshotAllocationsScaleWithReferences);
        runner.Run(TransitionToStalePreservesEarlierFrameAndChangesSelection);
        runner.Run(MarksExpiredReadingsAsStale);
        runner.Run(MarksExpiredPluginsAsStale);
        runner.Run(RemovesReadingsWhenADeviceDisconnects);
        runner.Run(RejectsNaNValues);
        runner.Run(RejectsInfiniteValues);
        runner.Run(RejectsReadingsOwnedByAnotherPlugin);
        runner.Run(RejectsDuplicateSensorIdentities);
        runner.Run(RejectsUnitsThatDoNotMatchTheMetric);
        runner.Run(RejectsFutureTimestamps);
    }

    private static void ReusesUnchangedReadingsAcrossSnapshots()
    {
        var store = SensorTestData.CreatePopulatedStore();
        var first = store.Snapshot(SensorTestData.Timestamp, []);
        var next = store.Snapshot(SensorTestData.Timestamp.AddMilliseconds(100), []);
        foreach (var reading in first.Sensors)
        {
            var unchanged = next.Sensors.Single(item => item.Identity == reading.Identity);
            TestAssert.True(ReferenceEquals(reading, unchanged), "An unchanged immutable reading was copied.");
        }
    }

    private static void NoSelectionSnapshotAllocationsScaleWithReferences()
    {
        const int frames = 32;
        const int sensors = 256;
        static long Measure(int count)
        {
            var store = SensorTestData.CreateStore();
            var now = SensorTestData.Timestamp;
            var readings = Enumerable.Range(0, count).Select(index => SensorTestData.CreatePowerReading(
                SensorTestData.WireViewPower with { DeviceId = $"fixture-{index}" })).ToArray();
            store.Update(SensorTestData.WireViewPower.PluginId, new PluginSample(PluginState.Ready, readings), now);
            for (var i = 0; i < frames; i++) store.Snapshot(now, []);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < frames; i++) store.Snapshot(now, []);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        var extraBytesPerReading = (Measure(sensors) - Measure(1)) / (frames * (sensors - 1));
        Console.WriteLine($"Snapshot allocation: {extraBytesPerReading} additional bytes per reading.");
        // Allow several references per reading for growing lists and returning an independent array.
        TestAssert.True(extraBytesPerReading <= 64,
            $"A fresh snapshot with no selections allocated {extraBytesPerReading} extra bytes per additional reading (budget 64).");
    }

    private static void TransitionToStalePreservesEarlierFrameAndChangesSelection()
    {
        var store = SensorTestData.CreatePopulatedStore();
        var now = SensorTestData.Timestamp;
        var selection = SensorTestData.CreatePowerSelection();
        var fresh = store.Snapshot(now, [selection]);
        var later = now.AddMinutes(1);
        store.Update(SensorTestData.AsusPower.PluginId, new PluginSample(PluginState.Ready,
            [SensorTestData.CreatePowerReading(SensorTestData.AsusPower) with { ObservedAt = later }]), later);
        var expired = store.Snapshot(later, [selection]);

        TestAssert.Equal(ReadingQuality.Stale, expired.Sensors.Single(item => item.Identity == SensorTestData.WireViewPower).Quality);
        TestAssert.Equal(SensorTestData.AsusPower.Key, expired.Selections.Single().SensorKey);
        TestAssert.Equal(ReadingQuality.Valid, fresh.Sensors.Single(item => item.Identity == SensorTestData.WireViewPower).Quality);
        TestAssert.Equal(SensorTestData.WireViewPower.Key, fresh.Selections.Single().SensorKey);
    }

    private static void MarksExpiredReadingsAsStale()
    {
        var store = SensorTestData.CreatePopulatedStore();
        var afterFreshnessLimit = SensorTestData.Timestamp.AddSeconds(6);

        var snapshot = store.Snapshot(afterFreshnessLimit, []);
        var wireViewReading = snapshot.Sensors.Single(reading => reading.Identity == SensorTestData.WireViewPower);

        TestAssert.Equal(ReadingQuality.Stale, wireViewReading.Quality);
    }

    private static void MarksExpiredPluginsAsStale()
    {
        var store = SensorTestData.CreatePopulatedStore();
        var afterFreshnessLimit = SensorTestData.Timestamp.AddSeconds(6);

        var snapshot = store.Snapshot(afterFreshnessLimit, []);
        var wireViewHealth = snapshot.Plugins.Single(plugin => plugin.PluginId == SensorTestData.WireViewPower.PluginId);

        TestAssert.Equal(PluginState.Stale, wireViewHealth.State);
    }

    private static void RemovesReadingsWhenADeviceDisconnects()
    {
        var store = SensorTestData.CreatePopulatedStore();
        var disconnectedSample = new PluginSample(PluginState.NoDevice, []);

        store.Update(SensorTestData.WireViewPower.PluginId, disconnectedSample, SensorTestData.Timestamp);
        var snapshot = store.Snapshot(SensorTestData.Timestamp, []);
        var hasWireViewReadings = snapshot.Sensors.Any(reading => reading.Identity.PluginId == SensorTestData.WireViewPower.PluginId);

        TestAssert.False(hasWireViewReadings, "Disconnected devices must not retain published readings.");
    }

    private static void RejectsNaNValues()
    {
        var reading = SensorTestData.CreatePowerReading(SensorTestData.WireViewPower, double.NaN);

        AssertReadingsAreRejected(reading);
    }

    private static void RejectsInfiniteValues()
    {
        var reading = SensorTestData.CreatePowerReading(SensorTestData.WireViewPower, double.PositiveInfinity);

        AssertReadingsAreRejected(reading);
    }

    private static void RejectsReadingsOwnedByAnotherPlugin()
    {
        var asusReading = SensorTestData.CreatePowerReading(SensorTestData.AsusPower);

        AssertReadingsAreRejected(asusReading);
    }

    private static void RejectsDuplicateSensorIdentities()
    {
        var firstReading = SensorTestData.CreatePowerReading(SensorTestData.WireViewPower, value: 400);
        var duplicateReading = SensorTestData.CreatePowerReading(SensorTestData.WireViewPower, value: 401);

        AssertReadingsAreRejected(firstReading, duplicateReading);
    }

    private static void RejectsUnitsThatDoNotMatchTheMetric()
    {
        var reading = SensorTestData.CreatePowerReading(SensorTestData.WireViewPower) with
        {
            Unit = SensorUnit.Amperes
        };

        AssertReadingsAreRejected(reading);
    }

    private static void RejectsFutureTimestamps()
    {
        var reading = SensorTestData.CreatePowerReading(SensorTestData.WireViewPower) with
        {
            ObservedAt = SensorTestData.Timestamp.AddMinutes(1)
        };

        AssertReadingsAreRejected(reading);
    }

    private static void AssertReadingsAreRejected(params SensorReading[] readings)
    {
        var store = SensorTestData.CreateStore();
        var sample = new PluginSample(PluginState.Ready, readings);

        TestAssert.Throws<ArgumentException>(() =>
        {
            store.Update(SensorTestData.WireViewPower.PluginId, sample, SensorTestData.Timestamp);
        });
    }
}
