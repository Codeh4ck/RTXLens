using RTXLens.Plugins.Asus.Contracts;
using RTXLens.Plugins.Asus.Contracts.Configuration;
using System.Text.Json;
using RTXLens.Contracts.Enums;
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

public sealed class AsusPlugin(PluginSettings settings, TimeProvider clock, IAsusGpuReader reader, AsusStream stream, ActiveSettingsProfileState? profiles = null) : ISensorPlugin
{
    public const string PluginId = AsusIdentity.Id;

    private readonly AsusAlertMonitor _alerts = new();
    private AsusOptions _options = AsusSettings.GetOptions(settings);
    private string? _deviceId;
    private AsusOptions? _effectiveOptions;
    private int _pollIntervalMs = settings.PollIntervalMs;

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
        _pollIntervalMs = settings.PollIntervalMs;
        Descriptor = Describe(settings);
    }

    public ValueTask<PluginSample> PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            reader.SetPollInterval(_pollIntervalMs);
            var data = reader.Read(_options.DeviceId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (data is null || data.Sensors.Length == 0)
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

            return ValueTask.FromResult(new PluginSample(PluginState.Ready, readings, Alerts: AsusAlertMapper.Create(data.DeviceId, _alerts, now)));
        }
        catch
        {
            ClearLatest();
            throw;
        }
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
        _alerts.Reset();
        stream.Hub.Reset();
    }

    private static PluginDescriptor Describe(PluginSettings settings) => new(PluginId, "ASUS RTX GPU",
        TimeSpan.FromMilliseconds(settings.PollIntervalMs), TimeSpan.FromMilliseconds(settings.StaleAfterMs));
}
