using RTXLens.Hardware.Nvidia;

namespace RTXLens.Tests.TestDoubles;

internal sealed class TestNvidiaHardwareSource : INvidiaHardwareSource
{
    public NvidiaDevice[] Devices { get; set; } = [];
    public NvidiaSensor[] Sensors { get; set; } = [];
    public int Reads { get; private set; }
    public int Resets { get; private set; }

    public Action? BeforeDiscovery { get; init; }

    public NvidiaDevice[] Discover()
    {
        BeforeDiscovery?.Invoke();
        return Devices;
    }

    public NvidiaSensor[] Read(NvidiaDevice device)
    {
        Reads++;
        return Sensors;
    }

    public void Reset() => Resets++;

    public void Dispose() { }
}
