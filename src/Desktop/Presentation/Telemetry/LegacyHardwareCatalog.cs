using RTXLens.Contracts.Discovery;
using RTXLens.Plugins.Asus.Contracts;
using RTXLens.Plugins.Msi.Contracts;
using RTXLens.Plugins.GenericNvidia.Contracts;
using RTXLens.Plugins.WireView.Contracts;

namespace RTXLens.Presentation.Telemetry;

// Services and recordings predating hardware discovery carry only the plugin ID.
public static class LegacyHardwareCatalog
{
    public static IReadOnlyDictionary<string, PluginHardwareMetadata> Sources { get; } = new Dictionary<string, PluginHardwareMetadata>
    {
        [AsusIdentity.Id] = AsusIdentity.Hardware,
        [MsiIdentity.Id] = MsiIdentity.Hardware,
        [NvidiaIdentity.Id] = NvidiaIdentity.Hardware,
        [WireViewIdentity.Id] = WireViewIdentity.Hardware
    };
}
