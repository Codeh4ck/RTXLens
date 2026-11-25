using RTXLens.Hardware.Nvidia.Diagnostics;

namespace RTXLens.Hardware.Nvidia;

public sealed class NativeNvidiaHardwareSource(TimeProvider clock) : INvidiaHardwareSource, INvidiaDiagnosticsReader
{
    private readonly NvidiaIdentityReader _devices = new();
    private readonly NvidiaDiagnosticsReader _management = new(clock);
    private readonly PawnIoThermalReader _thermals = new(clock);
    private NvidiaGpuDriverReader? _gpu;
    private AstralDriverReader? _astral;

    public Plugins.GenericNvidia.Contracts.Devices.NvidiaDiagnostics? Read(string deviceId) => _management.Read(deviceId);

    private (string Id, nint Handle)[] _topology = [];

    public NvidiaDevice[] Discover()
    {
        var devices = _devices.Discover();
        var topology = devices.Select(device => (device.Id, _devices.Handle(device.Id))).ToArray();

        if (_topology.Length > 0 && !_topology.SequenceEqual(topology))
        {
            _gpu = null;
            _astral = null;

            _thermals.Dispose();
            _management.Reset();
        }

        _topology = topology;
        return devices;
    }

    public NvidiaSensor[] Read(NvidiaDevice device)
    {
        var handle = _devices.Handle(device.Id);
        if (handle == 0)
        {
            return [];
        }

        _gpu ??= new NvidiaGpuDriverReader(_devices);

        var sensors = _management.ReadBoard(device.Id, out var pci);
        _gpu.Read(handle, device.Name, sensors);

        if (_thermals.Enabled && NvidiaGpuDriverReader.ThermalLayout(device.Name) is { Blackwell: true } && pci.ThermalAddress() is { } address)
        {
            _thermals.Read(address.Bus, address.Device, address.Function, sensors);
        }
        return [.. sensors];
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
        _topology = [];
        _astral = null;
        _gpu = null;

        _thermals.Dispose();
        _devices.Dispose();
        _management.Reset();
    }

    public void Dispose()
    {
        _thermals.Dispose();
        _management.Dispose();
        _devices.Dispose();
    }
}
