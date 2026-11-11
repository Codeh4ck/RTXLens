using RTXLens.Contracts.Enums;

namespace RTXLens.Contracts.Sensors;

public static class SensorUnits
{
    public static SensorUnit For(SensorMetric metric) => metric switch
    {
        SensorMetric.Power or SensorMetric.PowerCapability or SensorMetric.DefaultPowerLimit or SensorMetric.EnforcedPowerLimit
            or SensorMetric.MinimumPowerLimit or SensorMetric.MaximumPowerLimit => SensorUnit.Watts,
        SensorMetric.Current => SensorUnit.Amperes,
        SensorMetric.Voltage => SensorUnit.Volts,
        SensorMetric.Temperature => SensorUnit.Celsius,
        SensorMetric.Clock => SensorUnit.Megahertz,
        SensorMetric.FanSpeed => SensorUnit.Rpm,
        SensorMetric.Utilization or SensorMetric.ImbalancePercentage or SensorMetric.FanDuty => SensorUnit.Percent,
        SensorMetric.MemoryUsed or SensorMetric.MemoryTotal or SensorMetric.MemoryFree => SensorUnit.Megabytes,
        SensorMetric.ImbalanceDetected or SensorMetric.OvercurrentDetected or SensorMetric.FanAutomatic => SensorUnit.Boolean,
        SensorMetric.FaultStatus or SensorMetric.FaultLog or SensorMetric.ActiveClockReasons or SensorMetric.SupportedClockReasons
            or SensorMetric.SupportedPerformanceStates => SensorUnit.Bitmask,
        SensorMetric.ClockLimitDuration => SensorUnit.Seconds,
        SensorMetric.PerformanceState or SensorMetric.LinkGeneration or SensorMetric.MaximumSystemLinkGeneration
            or SensorMetric.MaximumGpuLinkGeneration or SensorMetric.LinkWidth or SensorMetric.MaximumLinkWidth
            or SensorMetric.ReplayCount or SensorMetric.ReplayRolloverCount => SensorUnit.Count,
        SensorMetric.LinkSpeed or SensorMetric.MaximumLinkSpeed => SensorUnit.MegabitsPerSecond,
        SensorMetric.ReceiveThroughput or SensorMetric.TransmitThroughput => SensorUnit.BytesPerSecond,
        _ => throw new ArgumentOutOfRangeException(nameof(metric))
    };
}
