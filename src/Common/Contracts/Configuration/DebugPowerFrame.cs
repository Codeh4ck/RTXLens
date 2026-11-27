using RTXLens.Contracts.Sensors;
using RTXLens.Contracts.Warnings;

namespace RTXLens.Contracts.Configuration;

public sealed record DebugPowerFrame(SensorReading[] Pins, DebugPowerFlag[] Flags)
{
    public DebugWarningHistoryFrame? History { get; init; }
}

public sealed record DebugWarningHistoryFrame(WarningState[] Warnings, SensorReading[] Readings);
public sealed record DebugPowerFlag(string PluginId, string DeviceId, string TypeId);
