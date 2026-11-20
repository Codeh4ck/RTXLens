using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Enums;

namespace RTXLens.Contracts.Discovery;

public enum GpuAssociation { None, SelectedGpu, GpuDevice, InlineMonitor, PowerSupply }

public sealed record PluginHardwareMetadata(string DisplayName, GpuAssociation Association = GpuAssociation.None)
{
    public PinProfileSupport? PinProfiles { get; init; }
}

public sealed record PinProfileSupport(SensorComponent Component)
{
    public string[] Connectors { get; init; } = [];
    public bool UseAutomaticLimits { get; init; }
    public bool SupportsVoltage { get; init; }
    public bool PublishPinLimits { get; init; }
    public bool ReplacePluginWarnings { get; init; }
    public bool UpdateDetectionFlags { get; init; }
    public string? OvercurrentAlert { get; init; }
    public string? ImbalanceAlert { get; init; }
    public Dictionary<string, string> LegacyWarningIds { get; init; } = [];
    public bool SuppliesLegacyProfileDefaults { get; init; }
    public DeviceWarningProfile? DefaultProfile { get; init; }

    public bool AcceptsConnector(string? connector) => Connectors.Length == 0 ? connector is null : connector is not null && Connectors.Contains(connector);
}
