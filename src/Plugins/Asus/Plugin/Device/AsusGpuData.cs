namespace RTXLens.Plugins.Asus.Device;

public sealed record AsusGpuData(string DeviceId, string HardwareName, AsusSensorValue[] Sensors, DateTimeOffset? ObservedAt = null)
{
    public bool CablePinsBelowThreshold { get; init; }
    public bool AlternativeInput { get; init; }
}
