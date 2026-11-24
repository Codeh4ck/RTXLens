using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Hardware.Nvidia;

namespace RTXLens.Hardware.Nvidia.Mapping;

public static class GenericNvidiaSnapshotMapper
{
    public static SensorReading[] Create(NvidiaSample sample, NvidiaSample? domainClocks = null)
    {
        var readings = new List<SensorReading>();

        foreach (var sensor in sample.Sensors)
        {
            var definition = (sensor.Name, sensor.Type) switch
            {
                ("GPU Core", SensorType.Clock) => (SensorComponent.GpuCore, SensorMetric.Clock),
                ("GPU Memory", SensorType.Clock) => (SensorComponent.GpuMemory, SensorMetric.Clock),
                ("GPU Core", SensorType.Temperature) => (SensorComponent.GpuCore, SensorMetric.Temperature),
                ("GPU Memory Junction", SensorType.Temperature) => (SensorComponent.GpuMemory, SensorMetric.Temperature),
                ("GPU Hot Spot", SensorType.Temperature) => (SensorComponent.GpuHotspot, SensorMetric.Temperature),
                ("GPU Core Voltage", SensorType.Voltage) => (SensorComponent.GpuCore, SensorMetric.Voltage),
                ("GPU Core", SensorType.Load) => (SensorComponent.GpuCore, SensorMetric.Utilization),
                ("GPU Memory", SensorType.Load) => (SensorComponent.GpuMemory, SensorMetric.Utilization),
                ("GPU Memory Used", SensorType.SmallData) => (SensorComponent.GpuMemory, SensorMetric.MemoryUsed),
                ("GPU Memory Total", SensorType.SmallData) => (SensorComponent.GpuMemory, SensorMetric.MemoryTotal),
                ("GPU Memory Free", SensorType.SmallData) => (SensorComponent.GpuMemory, SensorMetric.MemoryFree),
                ("12VHPWR Connector", SensorType.Power) => (SensorComponent.GpuConnector, SensorMetric.Power),
                ("12VHPWR Connector", SensorType.Current) => (SensorComponent.GpuConnector, SensorMetric.Current),
                ("12VHPWR Connector", SensorType.Voltage) => (SensorComponent.GpuConnector, SensorMetric.Voltage),
                _ => ((SensorComponent, SensorMetric)?)null
            };

            if (definition is { } pair)
            {
                Add(pair.Item1, pair.Item2, sensor.Value, pair.Item1 == SensorComponent.GpuConnector ? 1 : null);
            }
        }

        // A memory junction sensor takes priority over the older memory thermal API
        if (!readings.Any(reading => reading.Identity is { Component: SensorComponent.GpuMemory, Metric: SensorMetric.Temperature }))
        {
            var memory = sample.Sensors.FirstOrDefault(sensor => sensor is { Name: "GPU Memory", Type: SensorType.Temperature });
            if (memory is not null)
            {
                Add(SensorComponent.GpuMemory, SensorMetric.Temperature, memory.Value);
            }
        }

        if (!readings.Any(reading => reading is { Identity: { Component: SensorComponent.GpuConnector, Metric: SensorMetric.Voltage }, Value: > 0 }))
        {
            var pins = Enumerable.Range(1, 6)
                .Select(channel => sample.Sensors.FirstOrDefault(sensor => sensor.Name == $"12VHPWR Pin {channel}" && sensor.Type == SensorType.Voltage)?.Value)
                .ToArray();

            if (pins.All(value => value is { } number && float.IsFinite(number) && number > 0))
            {
                readings.RemoveAll(reading => reading.Identity is { Component: SensorComponent.GpuConnector, Metric: SensorMetric.Voltage });
                Add(SensorComponent.GpuConnector, SensorMetric.Voltage, pins.Average(value => value!.Value), 1);
            }
        }

        var connectorVoltage = readings.FirstOrDefault(reading => reading.Identity is { Component: SensorComponent.GpuConnector, Metric: SensorMetric.Voltage });

        if (connectorVoltage?.Value is not > 0)
        {
            var power = readings
                .FirstOrDefault(reading => reading.Identity is { Component: SensorComponent.GpuConnector, Metric: SensorMetric.Power })?.Value;

            var current = readings
                .FirstOrDefault(reading => reading.Identity is { Component: SensorComponent.GpuConnector, Metric: SensorMetric.Current })?.Value;

            if (power is > 0 && current is > 0 && double.IsFinite(power.Value / current.Value))
            {
                if (connectorVoltage is not null)
                {
                    readings.Remove(connectorVoltage);
                }

                readings.Add(new SensorReading(
                    new SensorIdentity(Plugins.GenericNvidia.Contracts.NvidiaIdentity.Id, sample.Device.Id, SensorComponent.GpuConnector, SensorMetric.Voltage, 1),
                    SensorUnit.Volts, power.Value / current.Value, sample.ObservedAt) { Derived = true });
            }
        }

        var fans = sample.Sensors.Where(sensor => sensor.Type == SensorType.Fan
            && (sensor.Name == "GPU" || sensor.Name.StartsWith("GPU Fan", StringComparison.Ordinal))).OrderBy(sensor => sensor.Index).ToArray();

        for (var index = 0; index < fans.Length; index++)
        {
            var fan = fans[index];
            Add(SensorComponent.GpuFan, SensorMetric.FanSpeed, fan.Value, index + 1);

            var duty = sample.Sensors.FirstOrDefault(sensor => sensor.Type == SensorType.Control && sensor.Index == fan.Index && sensor.Name.StartsWith("GPU Fan", StringComparison.Ordinal));

            if (duty is null && fans.Length == 1)
            {
                var controls = sample.Sensors.Where(sensor => sensor.Type == SensorType.Control && sensor.Name.StartsWith("GPU Fan", StringComparison.Ordinal)).ToArray();
                duty = controls.Length == 1 ? controls[0] : null;
            }

            if (duty is not null)
            {
                Add(SensorComponent.GpuFan, SensorMetric.FanDuty, duty.Value, index + 1);
            }
        }

        if (domainClocks?.Device.Id == sample.Device.Id)
        {
            foreach (var sensor in domainClocks.Sensors)
            {
                var component = sensor.Name switch
                {
                    "GPU Crossbar" => SensorComponent.GpuCrossbar,
                    "GPU System" => SensorComponent.GpuSystem,
                    _ => (SensorComponent?)null
                };

                if (component is { } domain)
                {
                    Add(domain, SensorMetric.Clock, sensor.Value, observedAt: domainClocks.ObservedAt);
                }
            }
        }

        return [.. readings];

        void Add(SensorComponent component, SensorMetric metric, float? value, int? channel = null, DateTimeOffset? observedAt = null)
        {
            double? number = value is { } finite && float.IsFinite(finite) ? finite : null;
            if (component == SensorComponent.GpuConnector && metric == SensorMetric.Voltage && number is not > 0)
            {
                number = null;
            }

            readings.Add(new SensorReading(new SensorIdentity(RTXLens.Plugins.GenericNvidia.Contracts.NvidiaIdentity.Id, sample.Device.Id, component, metric, channel),
                SensorUnits.For(metric), number, observedAt ?? sample.ObservedAt, number is null ? ReadingQuality.Unavailable : ReadingQuality.Valid));
        }
    }
}
