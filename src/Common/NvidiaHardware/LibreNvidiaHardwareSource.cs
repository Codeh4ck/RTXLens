using LibreHardwareMonitor.Hardware;

namespace RTXLens.Hardware.Nvidia;

public sealed class LibreNvidiaHardwareSource : INvidiaHardwareSource
{
    private readonly NvidiaIdentityReader _identities = new();
    private readonly Dictionary<string, IHardware> _hardware = new(StringComparer.Ordinal);
    private Computer? _computer;

    public NvidiaDevice[] Discover()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var identities = _identities.DiscoverDesktopIds();
        if (identities.Count == 0)
        {
            Reset();
            return [];
        }

        if (_computer is null)
        {
            _computer = new Computer { IsGpuEnabled = true };
            _computer.Open();
        }

        _hardware.Clear();
        var devices = new List<NvidiaDevice>();

        foreach (var gpu in _computer.Hardware)
        {
            var legacyId = gpu.Identifier.ToString();

            if (gpu.HardwareType != HardwareType.GpuNvidia || !gpu.Name.Contains("GeForce RTX", StringComparison.OrdinalIgnoreCase) || !identities.TryGetValue(legacyId, out var id))
            {
                continue;
            }

            _hardware[id] = gpu;
            devices.Add(new NvidiaDevice(id, legacyId, gpu.Name));
        }

        return [.. devices.OrderBy(device => device.Id, StringComparer.Ordinal)];
    }

    public NvidiaSensor[] Read(NvidiaDevice device)
    {
        if (!_hardware.TryGetValue(device.Id, out var gpu))
        {
            return [];
        }

        gpu.Update();

        return [.. gpu.Sensors.Select(sensor => new NvidiaSensor(sensor.Name, sensor.SensorType, sensor.Index,
            sensor.Value is { } value && float.IsFinite(value) ? value : null))];
    }

    public NvidiaSensor[] ReadDomainClocks(NvidiaDevice device) => _identities.ReadDomainClocks(device.Id);

    public void Reset()
    {
        _identities.ResetClocks();
        var computer = _computer;
        _computer = null;
        _hardware.Clear();
        computer?.Close();
    }

    public void Dispose()
    {
        Reset();
        _identities.Dispose();
    }
}
