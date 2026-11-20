using RTXLens.Contracts.Monitoring;
using RTXLens.Contracts.Discovery;

namespace RTXLens.Plugins.GenericNvidia.Contracts;

public static class NvidiaIdentity
{
    public const string Id = "generic-nvidia";
    public const string Route = ServiceRoutes.Plugins + "/" + Id;
    public const string DisplayName = "NVIDIA GPU status";
    public const string ShortName = "NVIDIA";
    public const string PluginName = "Nvidia";
    public static PluginHardwareMetadata Hardware { get; } = new(DisplayName, GpuAssociation.SelectedGpu);
}
