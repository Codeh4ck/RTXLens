using RTXLens.Contracts.Discovery;
using System.Text.Json.Serialization;

namespace RTXLens.Service.Mobile;

[Flags]
internal enum MobileWarningCategory
{
    Connector = 1,
    Temperature = 2,
    Safeguard = 4,
    Other = 8,
    All = Connector | Temperature | Safeguard | Other
}

internal sealed record MobileNotificationPreferences([property: JsonRequired] MobileWarningCategory Categories, [property: JsonRequired] bool ShowTitle)
{
    public static MobileNotificationPreferences Default { get; } = new(MobileWarningCategory.All, true);

    [JsonIgnore]
    public bool IsValid => (Categories & ~MobileWarningCategory.All) == 0;

    public bool Allows(MobileWarningCategory category) => (Categories & category) == category;

    public static MobileWarningCategory CategoryFor(PluginHardwareMetadata? hardware, string icon) => icon == "temperature"
        ? MobileWarningCategory.Temperature
        : hardware?.Association is GpuAssociation.GpuDevice or GpuAssociation.InlineMonitor ? MobileWarningCategory.Connector : MobileWarningCategory.Other;
}
