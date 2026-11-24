using RTXLens.Hardware.Nvidia;
using RTXLens.Plugins.Asus.Contracts;
using RTXLens.Plugins.Asus.Contracts.Configuration;
using System.Text.Json;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Contracts.Configuration;
using RTXLens.Core.Plugins;
using RTXLens.Core.Configuration;
using RTXLens.Plugins.Asus.Configuration;
using RTXLens.Plugins.Asus.Contracts.Common;
using RTXLens.Plugins.Asus.Device;
using RTXLens.Plugins.Asus.Mapping;
using RTXLens.Plugins.Asus.Analysis.Mapping;
using RTXLens.Plugins.Asus.Analysis.Monitoring;
using RTXLens.Plugins.Asus.Serialization;
using RTXLens.Plugins.Asus.Streaming;

namespace RTXLens.Plugins.Asus;

public sealed class AsusPlugin(PluginSettings settings, TimeProvider clock, IAsusGpuReader reader, AsusStream stream, ActiveSettingsProfileState? profiles = null) : ISensorPlugin, IPollTimingSource
{
    public const string PluginId = AsusIdentity.Id;

    private readonly NvidiaPollTimings _timings = new();
    public string DescribeLastPoll() => _timings.ToString();

    private readonly AsusAlertMonitor _alerts = new();
    private AsusOptions _options = AsusSettings.GetOptions(settings);
    private string? _deviceId;
    private const string UnknownPowerPath = "Power path undetermined";
    private string _cableStatus = UnknownPowerPath;
    private string? _cableObservation;
    private DateTimeOffset? _lastCableObservation;
    private SensorReading[] _lastPins = [];
    private AsusOptions? _effectiveOptions;

    public PluginDescriptor Descriptor { get; private set; } = Describe(settings);
    public GpuSensorSnapshot? Latest { get; private set; }
    public IReadOnlyList<AsusDeviceInfo> Devices => reader.Devices;

    public void ApplySettings(PluginSettings settings)
    {
        var options = AsusSettings.GetOptions(settings);
        if (options != _options)
        {
            _alerts.Reset();

            if (options.DeviceId != _options.DeviceId)
            {
                ClearLatest();
                _deviceId = null;
            }
        }

        _options = options;
        Descriptor = Describe(settings);
    }

    public ValueTask<PluginSample> PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            _timings.Clear();

            var data = reader.Read(_options.DeviceId, cancellationToken, _timings);
            cancellationToken.ThrowIfCancellationRequested();

            if (data is null)
            {
                ClearLatest();
                return ValueTask.FromResult(new PluginSample(PluginState.NoDevice, [], "Matching GPU or sensor data not available."));
            }

            if (_deviceId != data.DeviceId)
            {
                ClearLatest();
                _deviceId = data.DeviceId;
            }

            var now = clock.GetUtcNow();
            var currents = AsusSnapshotMapper.GetPinCurrents(data);
            var active = profiles?.Current;
            var effective = active?.AppliesToConnector(AsusIdentity.Hardware.Association, data.DeviceId) == true ? AsusProfileOptions.Apply(active.Profile, _options) : _options;

            if (_effectiveOptions != effective)
            {
                _alerts.Reset();
                _effectiveOptions = effective;
            }

            _alerts.Evaluate(currents, effective, now);

            Latest = AsusSnapshotMapper.CreateLegacySnapshot(data, _alerts, now);

            var readings = AsusSnapshotMapper.CreateReadings(data, _alerts, currents, data.ObservedAt ?? now);
            stream.Hub.Publish(JsonSerializer.SerializeToUtf8Bytes(Latest, AsusJsonContext.Default.GpuSensorSnapshot));

            var message = data.AlternativeInput ? CableStatus(data, currents)
                : currents.Count == 0 ? "Pin readings unavailable." : null;

            switch (currents.Count)
            {
                case 6:
                    _lastPins = [.. readings.Where(IsElectrical)];
                    break;
                case 0 when _lastPins.Length != 0:
                    readings = WithUnavailablePins(readings, _lastPins);
                    break;
            }

            return ValueTask.FromResult(new PluginSample(PluginState.Ready, readings, message, AsusAlertMapper.Create(data.DeviceId, _alerts, now)));
        }
        catch
        {
            ClearLatest();
            throw;
        }
    }

    private static bool IsElectrical(SensorReading reading) =>
        reading.Identity.Component is SensorComponent.GpuConnector or SensorComponent.GpuPin &&
        reading.Identity.Metric is SensorMetric.Current or SensorMetric.Voltage or SensorMetric.Power;

    private static SensorReading[] WithUnavailablePins(SensorReading[] current, SensorReading[] previous) =>
        [.. current.Where(reading => !IsElectrical(reading)),
         .. previous.Select(reading => reading with { Value = null, Quality = ReadingQuality.Unavailable })];

    private string CableStatus(AsusGpuData data, Dictionary<string, float> currents)
    {
        if (data.ObservedAt is not { } timestamp)
        {
            return UnknownPowerPath;
        }

        if (timestamp == _lastCableObservation)
        {
            return _cableStatus;
        }

        var boardPower = data.Sensors.FirstOrDefault(sensor => sensor.Definition is
            { Component: SensorComponent.GpuBoard, Metric: SensorMetric.Power })?.Value;

        var observed = currents.Any(pin => pin.Value > 0.1f) ? "16-pin cable active"
            : data.CablePinsBelowThreshold && boardPower > 75 ? "Cable load not observed" : UnknownPowerPath;

        var confirmed = observed == _cableObservation && _lastCableObservation is { } last
                                                      && timestamp > last && timestamp - last <= Descriptor.StaleAfter;

        _cableObservation = observed;
        _lastCableObservation = timestamp;
        _cableStatus = confirmed ? observed : UnknownPowerPath;

        return _cableStatus;
    }

    public void Dispose()
    {
        try
        {
            reader.Dispose();
        }
        finally
        {
            ClearLatest();
        }
    }

    private void ClearLatest()
    {
        Latest = null;
        _lastPins = [];
        _cableStatus = UnknownPowerPath;
        _cableObservation = null;
        _lastCableObservation = null;

        _alerts.Reset();
        stream.Hub.Reset();
    }

    private static PluginDescriptor Describe(PluginSettings settings) => new(PluginId, "ASUS RTX GPU",
        TimeSpan.FromMilliseconds(settings.PollIntervalMs), TimeSpan.FromMilliseconds(settings.StaleAfterMs));
}
