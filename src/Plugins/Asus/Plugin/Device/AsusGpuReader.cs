using RTXLens.Hardware.Nvidia;
using RTXLens.Plugins.Asus.Contracts.Common;
using RTXLens.Plugins.Asus.Mapping;

namespace RTXLens.Plugins.Asus.Device;

public sealed class AsusGpuReader(NvidiaTelemetryReader reader) : IAsusGpuReader
{
    public IReadOnlyList<AsusDeviceInfo> Devices { get; private set; } = [];

    public AsusGpuData? Read(string? deviceId, CancellationToken cancellationToken, NvidiaPollTimings? timings = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var devices = DiscoverAstral(reader);

        Devices = [.. devices.Select(device => new AsusDeviceInfo(device.Id, device.Name))];

        if (reader.ReadAstralPins(deviceId, cancellationToken, out var cachedGpu, timings) is not { } sample)
        {
            return null;
        }

        var selected = sample.Device;
        var readings = new List<AsusSensorValue>();

        AddReadings(sample);

        if (cachedGpu is not null)
        {
            AddReadings(cachedGpu);
        }

        return new AsusGpuData(selected.Id, selected.Name, [.. readings], sample.ObservedAt)
        {
            AlternativeInput = AstralBoard.HasAlternativeInput(selected.SubsystemId),
            CablePinsBelowThreshold = sample.CablePinsBelowThreshold
        };

        void AddReadings(NvidiaSample frame)
        {
            foreach (var sensor in frame.Sensors)
            {
                if (AsusSensorCatalog.Find(sensor.Name, sensor.Type) is { } definition)
                {
                    readings.Add(new AsusSensorValue(definition, sensor.Value, frame.ObservedAt));
                }
            }
        }
    }

    internal static NvidiaDevice[] DiscoverAstral(NvidiaTelemetryReader reader) => [.. reader.GetDevices().Where(device => device.IsAstral)];

    public void Dispose()
    {
        Devices = [];
        reader.ClearPinSelection();
    }
}
