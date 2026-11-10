using RTXLens.Plugins.WireView.Contracts.Common;

namespace RTXLens.Plugins.WireView.Device;

public interface IWireViewDeviceReader : IDisposable
{
    string PortName { get; }
    WireViewSensorData? TryReadSensors(CancellationToken cancellationToken);
    DeviceConfig? ReadConfig();
    DeviceConfig ApplyConfig(DeviceConfig changes) => throw new NotSupportedException("This WireView reader does not support configuration writes.");
    byte[]? ReadConfigRaw();
    bool ClearFaults(ushort faultStatusMask, ushort faultLogMask);
    void Reset();
}
