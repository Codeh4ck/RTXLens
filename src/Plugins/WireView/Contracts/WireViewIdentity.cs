using RTXLens.Contracts.Monitoring;
using RTXLens.Plugins.WireView.Contracts.Warnings;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Discovery;

namespace RTXLens.Plugins.WireView.Contracts;

public static class WireViewIdentity
{
    public const string Id = "wireview-pro2";
    public const string Route = ServiceRoutes.Plugins + "/" + Id;
    public const string DeviceConfigPath = "/device/config";
    public const string ClearFaultsPath = "/faults/clear";
    public const string DisplayName = "WireView Pro II";
    public const string PluginName = DisplayName;
    public static PluginHardwareMetadata Hardware { get; } = new(DisplayName, GpuAssociation.InlineMonitor)
    {
        PinProfiles = new(SensorComponent.GpuPin)
        {
            SupportsVoltage = true,
            PublishPinLimits = true,
            OvercurrentAlert = WireViewWarningIds.ProfilePinOvercurrent
        }
    };
}
