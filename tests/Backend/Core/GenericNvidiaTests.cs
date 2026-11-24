using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Enums;
using RTXLens.Hardware.Nvidia;
using RTXLens.Plugins.GenericNvidia;
using RTXLens.Plugins.GenericNvidia.Configuration;
using RTXLens.Plugins.GenericNvidia.Contracts.Devices;
using RTXLens.Hardware.Nvidia.Diagnostics;
using RTXLens.Hardware.Nvidia.Mapping;
using RTXLens.Tests.Infrastructure;
using RTXLens.Tests.TestDoubles;

namespace RTXLens.Tests.Core;

internal static class GenericNvidiaTests
{
    public static void Run(TestRunner runner)
    {
        runner.Run(ReadsFreshGpuValuesAtEveryScheduledPoll);
        runner.Run(PreservesSelectedIdentityWhenEnumerationChanges);
        runner.Run(MapsSingleAndMultipleFansByCoolerIdWithoutInventingDuty);
        runner.Run(MapsCoreVoltageAndHotspotWithoutPublishingBoardPower);
        runner.Run(StreamsNvmlDiagnosticsForTheSelectedGpu);
        runner.Run(ReadsCrossbarAndSystemByNativeDomainIndex);
        runner.Run(StreamsIndependentDomainClocksWithTheirMeasurementTime);
        runner.Run(InputVoltagePrefersMeasurementsAndUsesConnectorRatioOnlyAsFallback);
        runner.Run(RecognizesAstralCapabilitiesAndKeepsStableGpuIdentity);
        runner.Run(DecodesPhysicalPinOrderAndRejectsIncompleteFrames);
        runner.Run(PollsAstralPinsIndependentlyOfGpuStatus);
        runner.Run(PinFailurePreservesTimeAndDoesNotStopClocks);
        runner.Run(MigrationPreservesExistingThresholdsAndDefaults);
        runner.Run(LegacyThresholdPrecedenceSurvivesThePluginMove);
    }

    private static void ReadsFreshGpuValuesAtEveryScheduledPoll()
    {
        var clock = new TestTimeProvider();
        var source = Source();
        using var reader = new NvidiaTelemetryReader(source, clock);
        var first = reader.ReadGpu(null, TimeSpan.FromSeconds(5), null, CancellationToken.None, out _)!;
        clock.Advance(TimeSpan.FromMilliseconds(700));
        var second = reader.ReadGpu(first.Device.LegacyId, TimeSpan.FromSeconds(5), null, CancellationToken.None, out _)!;
        TestAssert.Equal(2, source.Reads);
        TestAssert.Equal(clock.GetUtcNow(), second.ObservedAt);
        clock.Advance(TimeSpan.FromMilliseconds(301));
        reader.ReadGpu(first.Device.Id, TimeSpan.FromSeconds(5), null, CancellationToken.None, out _);
        TestAssert.Equal(3, source.Reads);
    }

    private static void PreservesSelectedIdentityWhenEnumerationChanges()
    {
        var clock = new TestTimeProvider();
        var source = Source();
        using var reader = new NvidiaTelemetryReader(source, clock);
        var selected = reader.ReadGpu("pci-first", TimeSpan.FromSeconds(5), null, CancellationToken.None, out _)!;
        source.Devices = [new("pci-other", "/gpu-nvidia/0", "NVIDIA GeForce RTX 4080"), new("pci-first", "/gpu-nvidia/1", "NVIDIA GeForce RTX 5090")];
        reader.GetDevices(refresh: true);
        TestAssert.Equal(selected.Device.Id, reader.ReadGpu("pci-first", TimeSpan.FromSeconds(5), null, CancellationToken.None, out _)!.Device.Id);
        TestAssert.True(reader.ReadGpu("removed-device", TimeSpan.FromSeconds(5), null, CancellationToken.None, out _) is null, "A missing explicit selection must not switch GPUs.");
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

    private static void ReadsCrossbarAndSystemByNativeDomainIndex()
    {
        using var reader = new NvidiaIdentityReader();
        var readings = reader.ReadDomainClocks("pci-first", 1, Measure);
        TestAssert.Equal(2, readings.Length);
        TestAssert.Equal<float?>(2509, readings.Single(sensor => sensor.Name == "GPU Crossbar").Value);
        TestAssert.Equal<float?>(2869, readings.Single(sensor => sensor.Name == "GPU System").Value);
        return;

        static int Measure(nint handle, ref NvidiaIdentityReader.ClockMeasurement request)
        {
            TestAssert.Equal((nint)1, handle);
            TestAssert.Equal(0x1000Cu, request.Version);
            request.FrequencyKHz = request.DomainIndex switch
            {
                1 => 2_509_000, // Crossbar
                2 => 2_869_000, // System
                4 => 17_517_000, // Memory
                _ => throw new InvalidOperationException("Unexpected NVAPI clock domain.")
            };
            return 0;
        }
    }

    private static void StreamsIndependentDomainClocksWithTheirMeasurementTime()
    {
        var clock = new TestTimeProvider();
        var source = Source();
        source.DomainClocks = [new("GPU Crossbar", SensorType.Clock, 0, 2010.5f)];
        using var reader = new NvidiaTelemetryReader(source, clock);
        var factory = new GenericNvidiaPluginFactory(reader);
        var settings = factory.CreateDefaultSettings() with { PollIntervalMs = 250 };
        using var plugin = factory.Create(settings);

        var first = plugin.PollAsync(CancellationToken.None).Result;
        var crossbar = first.Sensors.Single(item => item.Identity.Component == SensorComponent.GpuCrossbar);
        TestAssert.Equal("pci-first", crossbar.Identity.DeviceId);
        TestAssert.Equal(SensorUnit.Megahertz, crossbar.Unit);
        TestAssert.Equal<double?>(2010.5, crossbar.Value);
        TestAssert.False(first.Sensors.Any(item => item.Identity.Component == SensorComponent.GpuSystem), "An unsupported clock should be omitted.");

        clock.Advance(TimeSpan.FromMilliseconds(settings.PollIntervalMs));
        source.DomainClocks = [new("GPU Crossbar", SensorType.Clock, 0, 2100)];
        var second = plugin.PollAsync(CancellationToken.None).Result;
        var updated = second.Sensors.Single(item => item.Identity.Component == SensorComponent.GpuCrossbar);
        TestAssert.Equal<double?>(2100, updated.Value);
        TestAssert.Equal(clock.GetUtcNow(), updated.ObservedAt);
        TestAssert.Equal(2, source.ClockReads);

        plugin.ApplySettings(settings with { PollIntervalMs = 100 });
        clock.Advance(TimeSpan.FromMilliseconds(100));
        source.DomainClocks = [new("GPU Crossbar", SensorType.Clock, 0, null), new("GPU System", SensorType.Clock, 0, 1700)];
        var next = plugin.PollAsync(CancellationToken.None).Result;
        TestAssert.Equal(ReadingQuality.Unavailable, next.Sensors.Single(item => item.Identity.Component == SensorComponent.GpuCrossbar).Quality);
        TestAssert.Equal<double?>(1700, next.Sensors.Single(item => item.Identity.Component == SensorComponent.GpuSystem).Value);
        TestAssert.Equal(3, source.ClockReads);
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
        source.Devices = [source.Devices[0] with { SubsystemId = 0x89E31043 }];
        reader.GetDevices(refresh: true);
        TestAssert.Equal<bool?>(true, factory.HardwarePresent(factory.CreateDefaultSettings()));
        TestAssert.Equal("pci-first", asus.Read("/gpu-nvidia/0", CancellationToken.None)!.DeviceId);
    }

    private static void DecodesPhysicalPinOrderAndRejectsIncompleteFrames()
    {
        var frame = new byte[24];
        for (var index = 0; index < 6; index++)
        {
            var pair = frame.AsSpan(4 * index, 4);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(pair, 12000);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(pair[2..], (ushort)(1000 * index));
        }
        var pins = AstralBoard.DecodePins(frame);
        TestAssert.Equal<float?>(5, pins.Single(sensor => sensor is { Name: "12VHPWR Pin 1", Type: SensorType.Current }).Value);
        TestAssert.Equal<float?>(0, pins.Single(sensor => sensor is { Name: "12VHPWR Pin 6", Type: SensorType.Current }).Value);
        TestAssert.Equal<float?>(180, pins.Single(sensor => sensor is { Name: "12VHPWR Connector", Type: SensorType.Power }).Value);
        TestAssert.Equal(0, AstralBoard.DecodePins(frame.AsSpan(1)).Length);
        Array.Fill(frame, (byte)0xFF);
        TestAssert.Equal(0, AstralBoard.DecodePins(frame).Length);
    }

    private static void PollsAstralPinsIndependentlyOfGpuStatus()
    {
        var clock = new TestTimeProvider();
        var source = Source();
        source.Devices = [source.Devices[0] with { SubsystemId = 0x89E31043 },
            new("pci-other", "/gpu-nvidia/1", "GeForce RTX 4080")];
        using var reader = new NvidiaTelemetryReader(source, clock);
        var asusFactory = new Plugins.Asus.Factories.AsusPluginFactory(clock, new Plugins.Asus.Streaming.AsusStream(), reader);
        using var asus = asusFactory.Create(asusFactory.CreateDefaultSettings());
        var factory = new GenericNvidiaPluginFactory(reader);
        using var generic = factory.Create(factory.CreateDefaultSettings());
        source.Pins = [.. Enumerable.Range(1, 6).Select(pin => new NvidiaSensor($"12VHPWR Pin {pin}", SensorType.Current, pin, 1))];
        var firstPins = asus.PollAsync(default).Result;
        TestAssert.Equal(0, source.Reads);
        TestAssert.Equal(0, source.ClockReads);
        TestAssert.Equal(1, source.PinReads);
        TestAssert.True(firstPins.Sensors.Any(reading => reading.Identity.Component == SensorComponent.GpuPin && reading.Value == 1),
            "Pins are available before NVIDIA starts.");
        generic.PollAsync(default).GetAwaiter().GetResult();
        var start = clock.GetUtcNow();
        for (var tick = 0; tick < 5; tick++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(200));
            var fast = asus.PollAsync(default).Result;
            TestAssert.Equal(clock.GetUtcNow(), fast.Sensors.First(sensor => sensor.Identity.Component == SensorComponent.GpuPin).ObservedAt);
            TestAssert.Equal(start, fast.Sensors.Single(sensor => sensor.Identity.Metric == SensorMetric.Clock).ObservedAt);
        }
        TestAssert.Equal(6, source.PinReads);
        TestAssert.Equal(1, source.Reads);
        TestAssert.Equal(1, source.ClockReads);
        generic.ApplySettings(factory.CreateDefaultSettings() with { PollIntervalMs = 200 });
        for (var tick = 0; tick < 5; tick++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(200));
            TestAssert.Equal(PluginState.Ready, generic.PollAsync(default).Result.State);
        }
        TestAssert.Equal(6, source.Reads);
        TestAssert.Equal(6, source.PinReads);
        TestAssert.Equal(6, source.ClockReads);
        generic.ApplySettings(GenericNvidiaSettings.WithOptions(factory.CreateDefaultSettings(),
            new Plugins.GenericNvidia.Contracts.Configuration.GenericNvidiaOptions { DeviceId = "pci-other" }));
        var other = generic.PollAsync(default).Result;
        TestAssert.True(other.Sensors.All(reading => reading.Identity.DeviceId == "pci-other"), "The NVIDIA selection is independent.");
        TestAssert.False(asus.PollAsync(default).Result.Sensors.Any(sensor => sensor.Identity.Metric == SensorMetric.Clock),
            "The Astral feed must never borrow another card's clock.");
        generic.Dispose();
        TestAssert.Equal(PluginState.Ready, asus.PollAsync(default).Result.State);
    }

    private static void PinFailurePreservesTimeAndDoesNotStopClocks()
    {
        var clock = new TestTimeProvider();
        var source = Source();
        source.Devices = [source.Devices[0] with { SubsystemId = 0x89E31043 }];
        source.Pins = [.. Enumerable.Range(1, 6).Select(pin => new NvidiaSensor($"12VHPWR Pin {pin}", SensorType.Current, pin, 1))];
        using var reader = new NvidiaTelemetryReader(source, clock);
        var factory = new Plugins.Asus.Factories.AsusPluginFactory(clock, new Plugins.Asus.Streaming.AsusStream(), reader);
        using var asus = factory.Create(factory.CreateDefaultSettings());
        var first = asus.PollAsync(default).Result;
        var measured = first.Sensors.First(sensor => sensor.Identity is { Component: SensorComponent.GpuPin, Metric: SensorMetric.Current });
        source.Pins = [];
        clock.Advance(TimeSpan.FromSeconds(10));
        var second = asus.PollAsync(default).Result;
        var unavailable = second.Sensors.Single(sensor => sensor.Identity == measured.Identity);
        TestAssert.Equal(measured.ObservedAt, unavailable.ObservedAt);
        TestAssert.Equal(ReadingQuality.Unavailable, unavailable.Quality);
        var nvidia = new GenericNvidiaPluginFactory(reader);
        using var gpu = nvidia.Create(nvidia.CreateDefaultSettings());
        TestAssert.True(unavailable.Value is null && gpu.PollAsync(default).Result.Sensors.Any(sensor => sensor.Value == 1500),
            "Missing pins must leave GPU Status live.");
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
