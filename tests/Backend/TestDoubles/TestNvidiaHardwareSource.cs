using RTXLens.Hardware.Nvidia;

namespace RTXLens.Tests.TestDoubles;

internal sealed class TestNvidiaHardwareSource : INvidiaHardwareSource
{
    public NvidiaDevice[] Devices { get; set; } = [];
    public NvidiaSensor[] Sensors { get; set; } = [];
    public int Reads { get; private set; }
    public NvidiaSensor[] Pins { get; set; } = [];
    public int PinReads { get; private set; }

    public NvidiaSensor[] ReadPins(NvidiaDevice device)
    {
        PinReads++;
        BeforePins?.Invoke();
        return Pins;
    }

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

    public NvidiaSensor[] DomainClocks { get; set; } = [];
    public Action? BeforeClocks { get; set; }
    public Action? BeforePins { get; set; }
    public int ClockReads { get; private set; }

    public NvidiaSensor[] ReadDomainClocks(NvidiaDevice device)
    {
        ClockReads++;
        BeforeClocks?.Invoke();
        return DomainClocks;
    }

    public void Reset() => Resets++;

    public void Dispose() { }
}
