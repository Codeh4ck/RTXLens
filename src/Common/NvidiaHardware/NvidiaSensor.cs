namespace RTXLens.Hardware.Nvidia;

public sealed record NvidiaSensor(string Name, SensorType Type, int Index, float? Value);

public enum SensorType { Clock, Temperature, Voltage, Load, SmallData, Power, Current, Fan, Control, Throughput }
