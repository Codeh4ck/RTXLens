namespace RTXLens.Hardware.Nvidia;

public interface INvidiaHardwareSource : IDisposable
{
    NvidiaDevice[] Discover();
    NvidiaSensor[] Read(NvidiaDevice device);
    NvidiaSensor[] ReadPins(NvidiaDevice device);
    NvidiaSensor[] ReadDomainClocks(NvidiaDevice device);
    void Reset();
}
