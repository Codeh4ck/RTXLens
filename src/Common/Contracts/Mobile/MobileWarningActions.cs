using RTXLens.Contracts.Configuration;

namespace RTXLens.Contracts.Mobile;

public sealed record MobileWarningActionSettings(bool EnableShutdown = false, bool EnableLowPower = false, string? GpuId = null, uint? PowerMilliwatts = null);
public sealed record MobileWarningPowerProfile(string Name, string GpuId, uint PowerMilliwatts);
public sealed record MobileWarningActionOptions(MobileWarningActionSettings Settings, PowerTarget[] Targets, MobileWarningPowerProfile[] Profiles);
public sealed record MobileWarningActionAvailability(bool ManagedOnDesktop, bool EnableShutdown, bool EnableLowPower, string? GpuId, uint? PowerMilliwatts);
public sealed record MobileWarningActionRequest(string? InstanceId, string? EpisodeId, string? Action, bool StatusOnly = false);
public sealed record MobileWarningActionResult(string State, uint? Milliwatts = null, string? Message = null);
