using RTXLens.Contracts.Warnings;
using RTXLens.Contracts.Monitoring;
using RTXLens.Plugins.Asus.Contracts.Warnings;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Discovery;

namespace RTXLens.Plugins.Asus.Contracts;

public static class AsusIdentity
{
    public const string Id = "asus-astral";
    public const string Route = ServiceRoutes.Plugins + "/" + Id;
    public const string DisplayName = "ROG Astral";
    public const string PluginName = "ASUS RTX ROG Astral GPU";
    public static PluginHardwareMetadata Hardware { get; } = new(DisplayName, GpuAssociation.GpuDevice)
    {
        PinProfiles = new(SensorComponent.GpuPin)
        {
            UseAutomaticLimits = true,
            SupportsVoltage = true,
            PublishPinLimits = true,
            ReplacePluginWarnings = true,
            UpdateDetectionFlags = true,
            OvercurrentAlert = AsusAlertKinds.WireOvercurrent,
            ImbalanceAlert = AsusAlertKinds.CurrentImbalance,
            LegacyWarningIds = new()
            {
                [PinWarningKind.Overcurrent] = AsusWarningIds.Overcurrent,
                [PinWarningKind.Imbalance] = AsusWarningIds.Imbalance,
                [PinWarningKind.LowVoltage] = AsusWarningIds.LowVoltage,
                [PinWarningKind.HighVoltage] = AsusWarningIds.HighVoltage
            },
            SuppliesLegacyProfileDefaults = true,
            DefaultProfile = new() { Id = "automatic-astral", Name = "Astral", PluginId = Id }
        }
    };
}
