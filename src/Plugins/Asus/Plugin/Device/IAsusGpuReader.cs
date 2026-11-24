using RTXLens.Hardware.Nvidia;
using RTXLens.Plugins.Asus.Contracts.Common;
namespace RTXLens.Plugins.Asus.Device;

public interface IAsusGpuReader : IDisposable
{
    IReadOnlyList<AsusDeviceInfo> Devices { get; }

    AsusGpuData? Read(string? deviceId, CancellationToken cancellationToken, NvidiaPollTimings? timings = null);
}
