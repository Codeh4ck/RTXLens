using System.Text.Json;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Core.Plugins;
using RTXLens.Plugins.WireView.Device;
using RTXLens.Plugins.WireView.Contracts.Output;
using RTXLens.Plugins.WireView.Mapping;
using RTXLens.Plugins.WireView.Services;
using RTXLens.Plugins.WireView.Streaming;
using RTXLens.Plugins.WireView.Serialization;

namespace RTXLens.Plugins.WireView;

public sealed class WireViewPlugin : ISensorPlugin
{
    public const string PluginId = "wireview-pro2";

    private readonly TimeProvider _clock;
    private readonly WireViewStream _stream;
    private readonly WireViewApplicationWatcher? _watcher;

    private int _yieldRequested;

    public PluginDescriptor Descriptor { get; private set; }
    public SensorResponse? Latest { get; private set; }
    public IWireViewDeviceReader Reader { get; }

    public WireViewPlugin(PluginSettings settings, TimeProvider clock, IWireViewDeviceReader reader, WireViewStream stream, bool watchApplication = false)
    {
        Reader = reader;
        _clock = clock;
        _stream = stream;
        Descriptor = CreateDescriptor(settings);

        if (watchApplication)
        {
            _watcher = new WireViewApplicationWatcher(() => Interlocked.Exchange(ref _yieldRequested, 1));
        }
    }

    public void ApplySettings(PluginSettings settings) => Descriptor = CreateDescriptor(settings);

    public async ValueTask<PluginSample> PollAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Interlocked.Exchange(ref _yieldRequested, 0) == 1)
        {
            Reader.Reset();
            Latest = null;
            _stream.Hub.Reset();
            await Task.Delay(3000, cancellationToken);
        }

        var data = Reader.TryReadSensors(cancellationToken);

        if (data is null)
        {
            Latest = null;
            _stream.Hub.Reset();
            return new PluginSample(PluginState.NoDevice, [], "WireView Pro II not available.");
        }

        var now = _clock.GetUtcNow();
        Latest = WireViewSnapshotMapper.Create(data, now);
        _stream.Hub.Publish(JsonSerializer.SerializeToUtf8Bytes(Latest, WireViewJsonContext.Default.SensorResponse));

        var deviceId = Reader.PortName.ToLowerInvariant();
        var sensors = new List<SensorReading>(28);

        for (var i = 0; i < 6; i++)
        {
            Add(SensorComponent.GpuPin, SensorMetric.Voltage, data.PinVoltage[i], i + 1);
            Add(SensorComponent.GpuPin, SensorMetric.Current, data.PinCurrent[i], i + 1);
            Add(SensorComponent.GpuPin, SensorMetric.Power, data.PinPower[i], i + 1);
        }

        // Use the firmware's native AvgVoltage reading, including when connector current is zero.
        double? connectorVoltage = data.ReportedAverageVoltageV is { } voltage && double.IsFinite(voltage) && voltage > 0
            ? voltage : null;

        Add(SensorComponent.GpuConnector, SensorMetric.Voltage, connectorVoltage, 1);
        Add(SensorComponent.GpuConnector, SensorMetric.Power, data.TotalPowerW, 1);
        Add(SensorComponent.GpuConnector, SensorMetric.Current, data.TotalCurrentA, 1);
        Add(SensorComponent.GpuConnector, SensorMetric.PowerCapability, data.PsuCapabilityW, 1);
        Add(SensorComponent.MonitorBoard, SensorMetric.Temperature, data.TempInC, 1);
        Add(SensorComponent.MonitorBoard, SensorMetric.Temperature, data.TempOutC, 2);
        Add(SensorComponent.ExternalProbe, SensorMetric.Temperature, data.ExtTemp1C < 0 ? null : data.ExtTemp1C, 1);
        Add(SensorComponent.ExternalProbe, SensorMetric.Temperature, data.ExtTemp2C < 0 ? null : data.ExtTemp2C, 2);
        Add(SensorComponent.MonitorBoard, SensorMetric.FaultStatus, data.FaultStatus);
        Add(SensorComponent.MonitorBoard, SensorMetric.FaultLog, data.FaultLog);

        return new PluginSample(PluginState.Ready, [.. sensors]);

        void Add(SensorComponent component, SensorMetric metric, double? value, int? channel = null) => sensors.Add(
            new SensorReading(new SensorIdentity(PluginId, deviceId, component, metric, channel), SensorUnits.For(metric), value, now,
                value is null ? ReadingQuality.Unavailable : ReadingQuality.Valid));
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        Reader.Dispose();
        Latest = null;
        _stream.Hub.Reset();
    }

    private static PluginDescriptor CreateDescriptor(PluginSettings settings) => new(PluginId, "WireView Pro II",
        TimeSpan.FromMilliseconds(settings.PollIntervalMs), TimeSpan.FromMilliseconds(settings.StaleAfterMs));
}