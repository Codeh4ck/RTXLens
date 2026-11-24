using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Plugins.Asus.Contracts.Common;
using RTXLens.Plugins.Asus.Device;
using RTXLens.Plugins.Asus.Analysis.Monitoring;

namespace RTXLens.Plugins.Asus.Mapping;

internal static class AsusSnapshotMapper
{
    public static Dictionary<string, float> GetPinCurrents(AsusGpuData data) => data.Sensors
        .Where(sensor => sensor is { Definition: { Component: SensorComponent.GpuPin, Metric: SensorMetric.Current }, Value: { } value } && float.IsFinite(value))
        .ToDictionary(sensor => sensor.Definition.FriendlyName, sensor => sensor.Value!.Value);

    public static GpuSensorSnapshot CreateLegacySnapshot(AsusGpuData data, AsusAlertMonitor alerts, DateTimeOffset now) => new()
    {
        Timestamp = now,
        Sensors =
        [
            .. data.Sensors.Where(sensor => sensor.Value is { } value && float.IsFinite(value)).Select(sensor => new GpuSensorReading
            {
                HardwareName = data.HardwareName,
                SensorName = sensor.Definition.FriendlyName,
                SensorType = sensor.Definition.SourceType.ToString(),
                Value = sensor.Value!.Value
            })
        ],
        CurrentImbalance = alerts.CurrentImbalance,
        WireOvercurrent = alerts.WireOvercurrent
    };

    public static SensorReading[] CreateReadings(AsusGpuData data, AsusAlertMonitor alerts, IReadOnlyDictionary<string, float> currents, DateTimeOffset now)
    {
        var readings = new List<SensorReading>(data.Sensors.Length + 8);
        foreach (var sensor in data.Sensors)
        {
            double? value = sensor.Value is { } number && float.IsFinite(number) ? number : null;
            readings.Add(CreateReading(data.DeviceId, sensor.Definition.Component, sensor.Definition.Metric, value, sensor.ObservedAt ?? now, sensor.Definition.Channel));
        }

        var voltages = data.Sensors.Where(sensor => sensor is { Definition: { Component: SensorComponent.GpuPin, Metric: SensorMetric.Voltage }, Value: > 0 }).ToArray();
        if (voltages.Length == 6 && voltages.Select(sensor => sensor.Definition.Channel).Distinct().Count() == 6)
        {
            readings.Add(CreateReading(data.DeviceId, SensorComponent.GpuConnector, SensorMetric.Voltage,
                voltages.Average(sensor => (double)sensor.Value!.Value), now, 1));
        }

        readings.Add(CreateReading(data.DeviceId, SensorComponent.GpuConnector, SensorMetric.ImbalancePercentage, alerts.ImbalancePercentage, now, 1));
        readings.Add(CreateFlagReading(data.DeviceId, SensorComponent.GpuConnector, SensorMetric.ImbalanceDetected, currents.Count >= 2, alerts.CurrentImbalance is not null, now, 1));

        for (var pin = 1; pin <= 6; pin++)
        {
            var name = $"Pin {pin}";
            var detected = alerts.WireOvercurrent?.PinValues.ContainsKey(name) == true;

            readings.Add(CreateFlagReading(data.DeviceId, SensorComponent.GpuPin, SensorMetric.OvercurrentDetected, currents.ContainsKey(name), detected, now, pin));
        }

        return [.. readings];
    }

    private static SensorReading CreateFlagReading(string deviceId, SensorComponent component, SensorMetric metric, bool available, bool detected, DateTimeOffset now, int channel)
    {
        double? value = null;
        if (available)
        {
            value = detected ? 1 : 0;
        }

        return CreateReading(deviceId, component, metric, value, now, channel);
    }

    private static SensorReading CreateReading(string deviceId, SensorComponent component, SensorMetric metric,
        double? value, DateTimeOffset observedAt, int? channel = null) => new(
        new SensorIdentity(AsusPlugin.PluginId, deviceId, component, metric, channel), SensorUnits.For(metric), value, observedAt,
        value is null ? ReadingQuality.Unavailable : ReadingQuality.Valid);
}