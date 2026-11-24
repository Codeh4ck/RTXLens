using LibreHardwareMonitor.Hardware;
using RTXLens.Hardware.Nvidia;

namespace RTXLens.Plugins.GenericNvidia;

internal sealed class LibreGpuFallback : INvidiaFallback
{
    private Computer? _computer;

    public NvidiaSensor[] Read(NvidiaDevice device)
    {
        if (_computer is null)
        {
            _computer = new Computer { IsGpuEnabled = true };
            _computer.Open();
        }

        var gpu = _computer.Hardware.FirstOrDefault(item => item.HardwareType == HardwareType.GpuNvidia
            && item.Identifier.ToString() == device.LegacyId);
        if (gpu is null)
        {
            return [];
        }

        gpu.Update();

        var readings = new List<NvidiaSensor>();
        foreach (var sensor in gpu.Sensors)
        {
            if (Enum.TryParse<Hardware.Nvidia.SensorType>(sensor.SensorType.ToString(), out var type))
            {
                readings.Add(new NvidiaSensor(sensor.Name, type, sensor.Index, sensor.Value));
            }
        }
        return [.. readings];
    }

    public void Reset()
    {
        var computer = _computer;
        _computer = null;
        computer?.Close();
    }

    public void Dispose() => Reset();
}
