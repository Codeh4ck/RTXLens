using RTXLens.Hardware.Nvidia.Diagnostics;

namespace RTXLens.Hardware.Nvidia;

public interface INvidiaFallback : IDisposable
{
    NvidiaSensor[] Read(NvidiaDevice device);
    void Reset();
}

public sealed class NativeNvidiaHardwareSource(TimeProvider clock, INvidiaFallback? fallback = null) : INvidiaHardwareSource, INvidiaDiagnosticsReader
{
    private readonly NvidiaIdentityReader _devices = new();
    private readonly NvidiaDiagnosticsReader _management = new(clock);
    private AstralDriverReader? _astral;

    public Plugins.GenericNvidia.Contracts.Devices.NvidiaDiagnostics? Read(string deviceId) => _management.Read(deviceId);

    private NvidiaDevice[] _knownDevices = [];

    public NvidiaDevice[] Discover()
    {
        var devices = _devices.Discover();
        if (!_knownDevices.SequenceEqual(devices))
        {
            fallback?.Reset();
            _knownDevices = devices;
        }
        return devices;
    }

    public NvidiaSensor[] Read(NvidiaDevice device)
    {
        if (!device.IsAstral)
        {
            return fallback?.Read(device) ?? [];
        }

        var handle = _devices.Handle(device.Id);
        if (handle == 0)
        {
            return [];
        }

        _astral ??= new AstralDriverReader(_devices);

        var sensors = _astral.Read(handle);
        return [.. _management.ReadBoard(device.Id), .. sensors];
    }

    public NvidiaSensor[] ReadPins(NvidiaDevice device)
    {
        var handle = _devices.Handle(device.Id);
        if (!device.IsAstral || handle == 0)
        {
            return [];
        }

        _astral ??= new AstralDriverReader(_devices);
        return _astral.ReadPins(handle);
    }

    public NvidiaSensor[] ReadDomainClocks(NvidiaDevice device) => _devices.ReadDomainClocks(device.Id);

    public void Reset()
    {
        _astral = null;
        _knownDevices = [];

        _devices.Dispose();
        _management.Reset();
        fallback?.Reset();
    }

    public void Dispose()
    {
        _management.Dispose();
        _devices.Dispose();
        // Dependency injection owns the optional fallback adapter.
    }
}
