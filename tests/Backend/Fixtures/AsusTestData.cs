using RTXLens.Hardware.Nvidia;
using RTXLens.Plugins.Asus.Device;
using RTXLens.Plugins.Asus.Mapping;

namespace RTXLens.Tests.Fixtures;

internal static class AsusTestData
{
    public const string FirstDeviceId = "/gpu-nvidia/0";
    public const string SecondDeviceId = "/gpu-nvidia/1";

    public static AsusGpuData Create(string deviceId = FirstDeviceId, float connectorPower = 450)
    {
        var sensors = new List<AsusSensorValue>
        {
            Reading("GPU Core", SensorType.Temperature, 60),
            Reading("GPU Core", SensorType.Clock, 2500),
            Reading("GPU Memory", SensorType.Clock, 14000),
            Reading("GPU Fan 1", SensorType.Fan, 1200),
            Reading("GPU Fan 2", SensorType.Fan, 1300),
            Reading("GPU Core", SensorType.Load, 80),
            Reading("GPU Memory Junction", SensorType.Temperature, 70),
            Reading("GPU Memory Total", SensorType.SmallData, 32768),
            Reading("GPU Memory Free", SensorType.SmallData, 16384),
            Reading("GPU Memory Used", SensorType.SmallData, 16384),
            Reading("GPU Memory", SensorType.Load, 50),
            Reading("GPU Package", SensorType.Power, connectorPower + 20),
            Reading("12VHPWR Connector", SensorType.Power, connectorPower),
            Reading("12VHPWR Connector", SensorType.Current, 37.5f)
        };

        for (var pin = 1; pin <= 6; pin++)
        {
            sensors.Add(Reading($"12VHPWR Pin {pin}", SensorType.Current, 6.25f));
            sensors.Add(Reading($"12VHPWR Pin {pin}", SensorType.Voltage, 12));
            sensors.Add(Reading($"12VHPWR Pin {pin}", SensorType.Power, 75));
        }

        return new AsusGpuData(deviceId, "NVIDIA GeForce RTX 5090", [.. sensors]);
    }

    public static AsusSensorValue Reading(string name, SensorType type, float? value) => new(
        AsusSensorCatalog.Find(name, type) ?? throw new ArgumentException($"Unknown test sensor: {name}/{type}."), value);
}
