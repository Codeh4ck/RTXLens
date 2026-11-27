using RTXLens.Plugins.GenericNvidia.Contracts;
using RTXLens.Plugins.Msi.Contracts;
using RTXLens.Plugins.Asus.Contracts;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Debugging.Models;
using RTXLens.Debugging.ViewModels;

namespace RTXLens.Debugging.Processing;

internal static class DebugReadingBuilder
{
    public static SensorReading[] Build(DebugPluginViewModel plugin, SensorReading[] live, string device, DateTimeOffset now)
    {
        var simulated = plugin.Mode == DebugSourceMode.Simulated;
        var readings = simulated ? new List<SensorReading>() : live.ToList();

        foreach (var metric in plugin.Metrics)
        {
            var definition = metric.Definition;
            var baseline = live.FirstOrDefault(reading => reading.Identity.DeviceId == device && definition.Matches(reading.Identity));

            metric.UpdateLive(baseline is { Quality: ReadingQuality.Valid } ? baseline.Value : null);

            if (!simulated && metric.Mode == DebugMetricMode.Live)
            {
                continue;
            }

            readings.RemoveAll(reading => reading.Identity.DeviceId == device && definition.Matches(reading.Identity));
            var quality = metric.Mode switch
            {
                DebugMetricMode.Unavailable => ReadingQuality.Unavailable,
                DebugMetricMode.Stale => ReadingQuality.Stale,
                _ => ReadingQuality.Valid
            };

            readings.Add(new SensorReading(definition.Identity(plugin.Id, device), definition.Unit,
                quality == ReadingQuality.Unavailable ? null : metric.Value, quality == ReadingQuality.Stale ? now.AddHours(-1) : now, quality));
        }

        if (simulated)
        {
            Derive(plugin, readings, now);
        }

        return [.. readings];
    }

    private static void Derive(DebugPluginViewModel plugin, List<SensorReading> readings, DateTimeOffset now)
    {
        switch (plugin.Id)
        {
            case NvidiaIdentity.Id:
                return;
            case MsiIdentity.Id:
                {
                    foreach (var connector in new[] { "1", "2" })
                    {
                        Set(SensorComponent.PsuConnector, SensorMetric.Current, Sum(SensorComponent.PsuPin, SensorMetric.Current, connector), connector: connector);
                    }

                    return;
                }
        }

        for (var pin = 1; pin <= 6; pin++)
        {
            Set(SensorComponent.GpuPin, SensorMetric.Power, Value(SensorComponent.GpuPin, SensorMetric.Current, pin) * Value(SensorComponent.GpuPin, SensorMetric.Voltage, pin), pin);
        }

        var current = Sum(SensorComponent.GpuPin, SensorMetric.Current);
        var power = Sum(SensorComponent.GpuPin, SensorMetric.Power);

        Set(SensorComponent.GpuConnector, SensorMetric.Current, current, 1);
        Set(SensorComponent.GpuConnector, SensorMetric.Power, power, 1);

        current = Value(SensorComponent.GpuConnector, SensorMetric.Current, 1);
        power = Value(SensorComponent.GpuConnector, SensorMetric.Power, 1);

        Set(SensorComponent.GpuConnector, SensorMetric.Voltage, current is > 0 ? power / current : null, 1);

        if (plugin.Id == AsusIdentity.Id)
        {
            var used = Value(SensorComponent.GpuMemory, SensorMetric.MemoryUsed);
            var total = Value(SensorComponent.GpuMemory, SensorMetric.MemoryTotal);

            Set(SensorComponent.GpuMemory, SensorMetric.MemoryFree, total >= used ? total - used : null);
            Set(SensorComponent.GpuMemory, SensorMetric.Utilization, total is > 0 ? used / total * 100 : null);
        }

        return;

        double? Value(SensorComponent component, SensorMetric metric, int? channel = null, string? connector = null) =>
            readings.FirstOrDefault(reading => reading.Identity.Component == component && reading.Identity.Metric == metric
                                                                                       && reading.Identity.Channel == channel &&
                                                                                       reading.Identity.ConnectorId == connector &&
                                                                                       reading.Quality == ReadingQuality.Valid)?.Value;

        void Set(SensorComponent component, SensorMetric metric, double? value, int? channel = null, string? connector = null)
        {
            var editor = plugin.Metrics.First(item => item.Definition.Component == component && item.Definition.Metric == metric
                                                                                             && item.Definition.Channel == channel &&
                                                                                             item.Definition.ConnectorId == connector);
            if (editor.Mode != DebugMetricMode.Live)
            {
                return;
            }

            var index = readings.FindIndex(reading => editor.Definition.Matches(reading.Identity));
            readings[index] = readings[index] with
            {
                Value = value, ObservedAt = now, Quality = value.HasValue ? ReadingQuality.Valid : ReadingQuality.Unavailable
            };
        }

        double? Sum(SensorComponent component, SensorMetric metric, string? connector = null)
        {
            var values = Enumerable.Range(1, 6).Select(pin => Value(component, metric, pin, connector)).ToArray();
            return values.All(value => value.HasValue) ? values.Sum(value => value!.Value) : null;
        }
    }
}
