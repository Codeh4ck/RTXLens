namespace RTXLens.Plugins.GenericNvidia.Contracts.Devices;

public sealed record NvidiaDiagnostics(
    string DeviceId,
    DateTimeOffset ObservedAt,
    ulong? ActiveClockReasons,
    ulong? SupportedClockReasons,
    NvidiaClockLimitDurations LimitDurations,
    int? PerformanceState,
    int[]? SupportedPerformanceStates,
    NvidiaPcieDiagnostics Pcie,
    NvidiaPowerLimits PowerLimits);

[Flags]
public enum NvidiaClockReason : ulong
{
    None = 0,
    Idle = 1UL << 0,
    ApplicationClocks = 1UL << 1,
    SoftwarePowerCap = 1UL << 2,
    HardwareSlowdown = 1UL << 3,
    SyncBoost = 1UL << 4,
    SoftwareThermalSlowdown = 1UL << 5,
    HardwareThermalSlowdown = 1UL << 6,
    HardwarePowerBrake = 1UL << 7,
    DisplayClocks = 1UL << 8,
    BoardLimit = 1UL << 9,
    Reliability = 1UL << 10
}

public sealed record NvidiaClockLimitDurations(
    double? SoftwarePowerCapSeconds,
    double? SoftwareThermalSlowdownSeconds,
    double? HardwareThermalSlowdownSeconds,
    double? HardwarePowerBrakeSeconds,
    double? SyncBoostSeconds);

public sealed record NvidiaPcieDiagnostics(
    uint? CurrentGeneration,
    uint? MaximumSystemGeneration,
    uint? MaximumGpuGeneration,
    uint? CurrentWidth,
    uint? MaximumSystemWidth,
    uint? CurrentLinkMegabitsPerSecond,
    uint? MaximumLinkMegabitsPerSecond,
    uint? TransmitKilobytesPerSecond,
    uint? ReceiveKilobytesPerSecond,
    uint? ReplayCount,
    uint? ReplayRolloverCount);

public sealed record NvidiaPowerLimits(
    double? DefaultPowerLimitWatts,
    double? EnforcedPowerLimitWatts,
    double? MinimumPowerLimitWatts,
    double? MaximumPowerLimitWatts);
