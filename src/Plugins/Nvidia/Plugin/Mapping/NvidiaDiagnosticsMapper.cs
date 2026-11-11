using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Plugins.GenericNvidia.Contracts.Devices;

namespace RTXLens.Plugins.GenericNvidia.Mapping;

internal static class NvidiaDiagnosticsMapper
{
    private const double MaximumExactMask = 9_007_199_254_740_991;

    public static SensorReading[] Create(NvidiaDiagnostics sample)
    {
        var readings = new List<SensorReading>();

        Add(SensorComponent.GpuPerformance, SensorMetric.ActiveClockReasons, Mask(sample.ActiveClockReasons));
        Add(SensorComponent.GpuPerformance, SensorMetric.SupportedClockReasons, Mask(sample.SupportedClockReasons));
        Add(SensorComponent.GpuPerformance, SensorMetric.PerformanceState, sample.PerformanceState);
        Add(SensorComponent.GpuPerformance, SensorMetric.SupportedPerformanceStates,
            sample.SupportedPerformanceStates?.Aggregate(0, (mask, state) => mask | (1 << state)));

        AddDuration(NvidiaClockReason.SoftwarePowerCap, sample.LimitDurations.SoftwarePowerCapSeconds);
        AddDuration(NvidiaClockReason.SoftwareThermalSlowdown, sample.LimitDurations.SoftwareThermalSlowdownSeconds);
        AddDuration(NvidiaClockReason.HardwareThermalSlowdown, sample.LimitDurations.HardwareThermalSlowdownSeconds);
        AddDuration(NvidiaClockReason.HardwarePowerBrake, sample.LimitDurations.HardwarePowerBrakeSeconds);
        AddDuration(NvidiaClockReason.SyncBoost, sample.LimitDurations.SyncBoostSeconds);

        var pcie = sample.Pcie;
        Add(SensorComponent.GpuPcie, SensorMetric.ReceiveThroughput, pcie.ReceiveKilobytesPerSecond * 1024d);
        Add(SensorComponent.GpuPcie, SensorMetric.TransmitThroughput, pcie.TransmitKilobytesPerSecond * 1024d);
        Add(SensorComponent.GpuPcie, SensorMetric.LinkGeneration, pcie.CurrentGeneration);
        Add(SensorComponent.GpuPcie, SensorMetric.MaximumSystemLinkGeneration, pcie.MaximumSystemGeneration);
        Add(SensorComponent.GpuPcie, SensorMetric.MaximumGpuLinkGeneration, pcie.MaximumGpuGeneration);
        Add(SensorComponent.GpuPcie, SensorMetric.LinkWidth, pcie.CurrentWidth);
        Add(SensorComponent.GpuPcie, SensorMetric.MaximumLinkWidth, pcie.MaximumSystemWidth);
        Add(SensorComponent.GpuPcie, SensorMetric.LinkSpeed, pcie.CurrentLinkMegabitsPerSecond);
        Add(SensorComponent.GpuPcie, SensorMetric.MaximumLinkSpeed, pcie.MaximumLinkMegabitsPerSecond);
        Add(SensorComponent.GpuPcie, SensorMetric.ReplayCount, pcie.ReplayCount);
        Add(SensorComponent.GpuPcie, SensorMetric.ReplayRolloverCount, pcie.ReplayRolloverCount);

        Add(SensorComponent.GpuPowerLimits, SensorMetric.DefaultPowerLimit, sample.PowerLimits.DefaultPowerLimitWatts);
        Add(SensorComponent.GpuPowerLimits, SensorMetric.EnforcedPowerLimit, sample.PowerLimits.EnforcedPowerLimitWatts);
        Add(SensorComponent.GpuPowerLimits, SensorMetric.MinimumPowerLimit, sample.PowerLimits.MinimumPowerLimitWatts);
        Add(SensorComponent.GpuPowerLimits, SensorMetric.MaximumPowerLimit, sample.PowerLimits.MaximumPowerLimitWatts);

        return [.. readings];

        static double? Mask(ulong? value) => value <= MaximumExactMask ? value : null;

        void AddDuration(NvidiaClockReason reason, double? seconds) =>
            Add(SensorComponent.GpuPerformance, SensorMetric.ClockLimitDuration, seconds, (int)reason);

        void Add(SensorComponent component, SensorMetric metric, double? value, int? channel = null)
        {
            readings.Add(new SensorReading(new SensorIdentity(GenericNvidiaPlugin.PluginId, sample.DeviceId, component, metric, channel),
                SensorUnits.For(metric), value, sample.ObservedAt, value.HasValue ? ReadingQuality.Valid : ReadingQuality.Unavailable));
        }
    }
}
