using LibreHardwareMonitor.Hardware;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Enums;
using RTXLens.Hardware.Nvidia;
using RTXLens.Plugins.GenericNvidia;
using RTXLens.Plugins.GenericNvidia.Configuration;
using RTXLens.Plugins.GenericNvidia.Contracts.Devices;
using RTXLens.Plugins.GenericNvidia.Diagnostics;
using RTXLens.Plugins.GenericNvidia.Mapping;
using RTXLens.Tests.Infrastructure;
using RTXLens.Tests.TestDoubles;

namespace RTXLens.Tests.Core;

internal static class GenericNvidiaTests
{
    public static void Run(TestRunner runner)
    {
        runner.Run(SharesOneAcquisitionAndRetainsTheOriginalObservationTime);
        runner.Run(PreservesSelectedIdentityWhenEnumerationChanges);
        runner.Run(MapsSingleAndMultipleFansByCoolerIdWithoutInventingDuty);
        runner.Run(MapsCoreVoltageAndHotspotWithoutPublishingBoardPower);
        runner.Run(StreamsNvmlDiagnosticsForTheSelectedGpu);
        runner.Run(InputVoltagePrefersMeasurementsAndUsesConnectorRatioOnlyAsFallback);
        runner.Run(RecognizesAstralCapabilitiesAndKeepsStableGpuIdentity);
        runner.Run(MigrationPreservesExistingThresholdsAndDefaults);
        runner.Run(LegacyThresholdPrecedenceSurvivesThePluginMove);
    }

    private static void SharesOneAcquisitionAndRetainsTheOriginalObservationTime()
    {
        var clock = new TestTimeProvider();
        var source = Source();
        using var reader = new NvidiaTelemetryReader(source, clock);
        var first = reader.Read(null, 1000, CancellationToken.None)!;
        clock.Advance(TimeSpan.FromMilliseconds(700));
        var second = reader.Read(first.Device.LegacyId, 1000, CancellationToken.None)!;
        TestAssert.Equal(1, source.Reads);
        TestAssert.Equal(first.ObservedAt, second.ObservedAt);
        clock.Advance(TimeSpan.FromMilliseconds(301));
        reader.Read(first.Device.Id, 1000, CancellationToken.None);
        TestAssert.Equal(2, source.Reads);
    }

    private static void PreservesSelectedIdentityWhenEnumerationChanges()
    {
        var clock = new TestTimeProvider();
        var source = Source();
        using var reader = new NvidiaTelemetryReader(source, clock);
        var selected = reader.Read("pci-first", 1000, CancellationToken.None)!;
        source.Devices = [new("pci-other", "/gpu-nvidia/0", "NVIDIA GeForce RTX 4080"), new("pci-first", "/gpu-nvidia/1", "NVIDIA GeForce RTX 5090")];
        reader.GetDevices(refresh: true);
        TestAssert.Equal(selected.Device.Id, reader.Read("pci-first", 1000, CancellationToken.None)!.Device.Id);
        TestAssert.True(reader.Read("removed-device", 1000, CancellationToken.None) is null, "A missing explicit selection must not switch GPUs.");
    }

    private static void MapsSingleAndMultipleFansByCoolerIdWithoutInventingDuty()
    {
        var sample = Sample([
            new("GPU Fan", SensorType.Fan, 4, 1200),
            new("GPU Fan", SensorType.Control, 4, 40)]);
        var readings = GenericNvidiaSnapshotMapper.Create(sample);
        TestAssert.Equal(1, readings.Single(reading => reading.Identity.Metric == SensorMetric.FanSpeed).Identity.Channel);
        TestAssert.Equal<double?>(40, readings.Single(reading => reading.Identity.Metric == SensorMetric.FanDuty).Value);

        readings = GenericNvidiaSnapshotMapper.Create(Sample([
            new("GPU Fan 2", SensorType.Fan, 8, 1500),
            new("GPU Fan 1", SensorType.Fan, 4, 1200),
            new("GPU Fan 2", SensorType.Control, 8, 50)]));
        TestAssert.Equal<double?>(1200, readings.Single(reading => reading.Identity is { Metric: SensorMetric.FanSpeed, Channel: 1 }).Value);
        TestAssert.Equal(2, readings.Single(reading => reading.Identity.Metric == SensorMetric.FanDuty).Identity.Channel);

        readings = GenericNvidiaSnapshotMapper.Create(Sample([new("GPU", SensorType.Fan, 1, 0)]));
        TestAssert.Equal<double?>(0, readings.Single().Value);
    }

    private static void MapsCoreVoltageAndHotspotWithoutPublishingBoardPower()
    {
        var readings = GenericNvidiaSnapshotMapper.Create(Sample([
            new("GPU Core Voltage", SensorType.Voltage, 0, 0.925f),
            new("GPU Hot Spot", SensorType.Temperature, 2, 75),
            new("GPU Package", SensorType.Power, 0, 400)]));
        TestAssert.Equal(2, readings.Length);
        TestAssert.Equal(SensorUnit.Volts, readings.Single(reading => reading.Identity.Component == SensorComponent.GpuCore).Unit);
        TestAssert.Equal<double?>(75, readings.Single(reading => reading.Identity.Component == SensorComponent.GpuHotspot).Value);
    }

    private static void StreamsNvmlDiagnosticsForTheSelectedGpu()
    {
        var clock = new TestTimeProvider();
        var source = Source();
        source.Sensors = [.. source.Sensors, new("GPU PCIe Rx", SensorType.Throughput, 0, 1024)];
        using var reader = new NvidiaTelemetryReader(source, clock);
        var diagnostics = new FakeDiagnosticsReader(clock.GetUtcNow());
        var factory = new GenericNvidiaPluginFactory(reader, diagnostics);
        using var plugin = factory.Create(factory.CreateDefaultSettings());

        var sample = plugin.PollAsync(CancellationToken.None).GetAwaiter().GetResult();
        var store = new RTXLens.Core.SensorStateStore([plugin]);
        store.Update(GenericNvidiaPlugin.PluginId, sample, clock.GetUtcNow());
        var receive = sample.Sensors.Single(reading => reading.Identity.Metric == SensorMetric.ReceiveThroughput);
        var transmit = sample.Sensors.Single(reading => reading.Identity.Metric == SensorMetric.TransmitThroughput);
        var state = sample.Sensors.Single(reading => reading.Identity.Metric == SensorMetric.PerformanceState);

        TestAssert.Equal(PluginState.Ready, sample.State);
        TestAssert.Equal("pci-first", receive.Identity.DeviceId);
        TestAssert.Equal(SensorComponent.GpuPcie, receive.Identity.Component);
        TestAssert.Equal(SensorUnit.BytesPerSecond, receive.Unit);
        TestAssert.Equal<double?>(5_120_000_000d, receive.Value);
        TestAssert.Equal<double?>(0, transmit.Value);
        TestAssert.Equal(ReadingQuality.Valid, transmit.Quality);
        TestAssert.Equal<double?>(0, state.Value);
        TestAssert.Equal(ReadingQuality.Unavailable,
            sample.Sensors.Single(reading => reading.Identity.Metric == SensorMetric.ReplayCount).Quality);
    }

    private sealed class FakeDiagnosticsReader(DateTimeOffset observedAt) : INvidiaDiagnosticsReader
    {
        public NvidiaDiagnostics Read(string deviceId) => new(deviceId, observedAt, (ulong)NvidiaClockReason.SoftwarePowerCap,
            null, new NvidiaClockLimitDurations(2, null, null, null, null), 0, [0, 8],
            new NvidiaPcieDiagnostics(5, 5, 5, 16, 16, 32000, 32000, 0, 5_000_000, null, null),
            new NvidiaPowerLimits(600, 575, 400, 600));
    }

    private static void InputVoltagePrefersMeasurementsAndUsesConnectorRatioOnlyAsFallback()
    {
        var sensors = new[] { new NvidiaSensor("12VHPWR Connector", SensorType.Power, 0, 400), new NvidiaSensor("12VHPWR Connector", SensorType.Current, 0, 20) };
        TestAssert.Equal<double?>(20, GenericNvidiaSnapshotMapper.Create(Sample(sensors)).Single(reading => reading.Identity.Metric == SensorMetric.Voltage).Value);
        TestAssert.True(GenericNvidiaSnapshotMapper.Create(Sample(sensors)).Single(reading => reading.Identity.Metric == SensorMetric.Voltage).Derived,
            "The ratio sensor must preserve internal provenance without changing the displayed voltage label.");
        var stopped = new[] { sensors[0], sensors[1] with { Value = 0 } };
        TestAssert.False(GenericNvidiaSnapshotMapper.Create(Sample(stopped)).Any(reading => reading.Identity.Metric == SensorMetric.Voltage),
            "Zero connector current must not produce voltage from a division by zero.");
        var board = new[] { new NvidiaSensor("GPU Package", SensorType.Power, 0, 400), sensors[1] };
        TestAssert.False(GenericNvidiaSnapshotMapper.Create(Sample(board)).Any(reading => reading.Identity.Metric == SensorMetric.Voltage),
            "Board power must never be used to derive connector voltage.");
        sensors = [.. sensors, .. Enumerable.Range(1, 6).Select(pin => new NvidiaSensor($"12VHPWR Pin {pin}", SensorType.Voltage, pin, 12))];
        TestAssert.Equal<double?>(12, GenericNvidiaSnapshotMapper.Create(Sample(sensors)).Single(reading => reading.Identity.Metric == SensorMetric.Voltage).Value);
    }

    private static void RecognizesAstralCapabilitiesAndKeepsStableGpuIdentity()
    {
        var source = Source();
        using var reader = new NvidiaTelemetryReader(source, new TestTimeProvider());
        using var asus = new Plugins.Asus.Device.AsusGpuReader(reader);
        var factory = new Plugins.Asus.Factories.AsusPluginFactory(new TestTimeProvider(),
            new Plugins.Asus.Streaming.AsusStream(), reader);
        TestAssert.True(asus.Read(null, CancellationToken.None) is null, "An RTX 5090 name alone must not qualify as Astral.");
        TestAssert.Equal<bool?>(false, factory.HardwarePresent(factory.CreateDefaultSettings()));
        source.Sensors = [.. source.Sensors, new("12VHPWR Pin 1", SensorType.Current, 1, 0)];
        reader.GetDevices(refresh: true);
        TestAssert.Equal<bool?>(true, factory.HardwarePresent(factory.CreateDefaultSettings()));
        TestAssert.Equal("pci-first", asus.Read("/gpu-nvidia/0", CancellationToken.None)!.DeviceId);
    }

    private static void MigrationPreservesExistingThresholdsAndDefaults()
    {
        using var reader = new NvidiaTelemetryReader(Source(), new TestTimeProvider());
        var factory = new GenericNvidiaPluginFactory(reader);
        var fresh = GenericNvidiaSettings.GetOptions(factory.CreateDefaultSettings());
        TestAssert.Equal<double?>(81, fresh.CoreTemperatureWarningC);
        TestAssert.Equal<double?>(85, fresh.MemoryTemperatureWarningC);
        var legacy = new ServiceConfiguration { Plugins = new() { ["asus-astral"] = new() } };
        var upgraded = factory.MigrateConfiguration(legacy);
        var migrated = GenericNvidiaSettings.GetOptions(upgraded.Plugins[factory.Id]);
        TestAssert.Equal<double?>(84, migrated.CoreTemperatureWarningC);
        TestAssert.Equal<double?>(95, migrated.MemoryTemperatureWarningC);
        TestAssert.False(legacy.Plugins.ContainsKey(factory.Id), "Migration must not mutate its baseline.");
    }

    private static void LegacyThresholdPrecedenceSurvivesThePluginMove()
    {
        using var reader = new NvidiaTelemetryReader(Source(), new TestTimeProvider());
        var factory = new GenericNvidiaPluginFactory(reader);
        var legacy = new ServiceConfiguration
        {
            Plugins = new() { ["asus-astral"] = new() { Options = System.Text.Json.JsonSerializer.SerializeToElement(new { CoreTemperatureWarningC = 90 }) } },
            Warnings = new()
            {
                AdditionalSettings = new()
                {
                    ["coreTemperatureC"] = System.Text.Json.JsonSerializer.SerializeToElement(81),
                    ["memoryTemperatureC"] = System.Text.Json.JsonSerializer.SerializeToElement(87)
                }
            }
        };
        var migrated = factory.MigrateConfiguration(legacy);
        var options = GenericNvidiaSettings.GetOptions(migrated.Plugins[factory.Id]);
        TestAssert.Equal<double?>(90, options.CoreTemperatureWarningC);
        TestAssert.Equal<double?>(87, options.MemoryTemperatureWarningC);
        TestAssert.True(migrated.Warnings.AdditionalSettings is null, "Consumed legacy settings must not override the new plugin again.");
    }

    private static TestNvidiaHardwareSource Source() => new()
    {
        Devices = [new("pci-first", "/gpu-nvidia/0", "NVIDIA GeForce RTX 5090")],
        Sensors = [new("GPU Core", SensorType.Clock, 0, 1500)]
    };

    private static NvidiaSample Sample(NvidiaSensor[] sensors) => new(new("gpu", "/gpu-nvidia/0", "NVIDIA GeForce RTX 5090"), DateTimeOffset.UtcNow, sensors);
}
