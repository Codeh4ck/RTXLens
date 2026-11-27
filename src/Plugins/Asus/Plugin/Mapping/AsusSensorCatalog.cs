using System.Collections.Frozen;
using RTXLens.Hardware.Nvidia;
using RTXLens.Contracts.Enums;

namespace RTXLens.Plugins.Asus.Mapping;

public static class AsusSensorCatalog
{
    private static readonly FrozenDictionary<(string, SensorType), AsusSensorDefinition> Definitions = CreateDefinitions();

    public static AsusSensorDefinition? Find(string name, SensorType type) => Definitions.GetValueOrDefault((name, type));

    private static FrozenDictionary<(string, SensorType), AsusSensorDefinition> CreateDefinitions()
    {
        var definitions = new List<AsusSensorDefinition>
        {
            new("GPU Core", SensorType.Temperature, "GPU Core Temp", SensorComponent.GpuCore, SensorMetric.Temperature),
            new("GPU Core", SensorType.Clock, "GPU Core Clock", SensorComponent.GpuCore, SensorMetric.Clock),
            new("GPU Memory", SensorType.Clock, "GPU Memory Clock", SensorComponent.GpuMemory, SensorMetric.Clock),
            new("GPU Fan 1", SensorType.Fan, "Fan 1 Speed", SensorComponent.GpuFan, SensorMetric.FanSpeed, 1),
            new("GPU Fan 2", SensorType.Fan, "Fan 2 Speed", SensorComponent.GpuFan, SensorMetric.FanSpeed, 2),
            new("GPU Fan 1", SensorType.Control, "Fan 1 Duty", SensorComponent.GpuFan, SensorMetric.FanDuty, 1),
            new("GPU Fan 2", SensorType.Control, "Fan 2 Duty", SensorComponent.GpuFan, SensorMetric.FanDuty, 2),
            new("GPU Core", SensorType.Load, "GPU Usage", SensorComponent.GpuCore, SensorMetric.Utilization),
            new("GPU Memory Junction", SensorType.Temperature, "VRAM Temp", SensorComponent.GpuMemory, SensorMetric.Temperature),
            new("GPU Memory Total", SensorType.SmallData, "VRAM Total", SensorComponent.GpuMemory, SensorMetric.MemoryTotal),
            new("GPU Memory Free", SensorType.SmallData, "VRAM Free", SensorComponent.GpuMemory, SensorMetric.MemoryFree),
            new("GPU Memory Used", SensorType.SmallData, "VRAM Used", SensorComponent.GpuMemory, SensorMetric.MemoryUsed),
            new("GPU Memory", SensorType.Load, "VRAM Usage", SensorComponent.GpuMemory, SensorMetric.Utilization),
            new("GPU Package", SensorType.Power, "GPU Power Draw", SensorComponent.GpuBoard, SensorMetric.Power),
            new("12VHPWR Connector", SensorType.Power, "Total Power", SensorComponent.GpuConnector, SensorMetric.Power, 1),
            new("12VHPWR Connector", SensorType.Current, "Total Current", SensorComponent.GpuConnector, SensorMetric.Current, 1)
        };

        for (var pin = 1; pin <= 6; pin++)
        {
            var name = $"12VHPWR Pin {pin}";
            definitions.Add(new AsusSensorDefinition(name, SensorType.Voltage, $"Pin {pin}", SensorComponent.GpuPin, SensorMetric.Voltage, pin));
            definitions.Add(new AsusSensorDefinition(name, SensorType.Current, $"Pin {pin}", SensorComponent.GpuPin, SensorMetric.Current, pin));
            definitions.Add(new AsusSensorDefinition(name, SensorType.Power, $"Pin {pin}", SensorComponent.GpuPin, SensorMetric.Power, pin));
        }

        return definitions.ToFrozenDictionary(definition => (definition.SourceName, definition.SourceType));
    }
}
