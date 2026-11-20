using RTXLens.Contracts.Monitoring;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Discovery;

namespace RTXLens.Plugins.Msi.Contracts;

public static class MsiIdentity
{
    public const string Id = "msi-psu";
    public const string Route = ServiceRoutes.Plugins + "/" + Id;
    public const string DisplayName = "MSI PSU";
    public const string PluginName = "MSI MPG Ai1300TS / Ai1600TS";
    public static PluginHardwareMetadata Hardware { get; } = new(DisplayName, GpuAssociation.PowerSupply)
    {
        PinProfiles = new(SensorComponent.PsuPin) { Connectors = ["1", "2"] }
    };
}
