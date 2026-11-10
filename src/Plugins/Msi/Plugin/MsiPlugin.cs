using Microsoft.Extensions.Logging;
using RTXLens.Contracts.Configuration;
using RTXLens.Core.Diagnostics;
using RTXLens.Plugins.Msi.Control;
using RTXLens.Contracts.Enums;
using RTXLens.Core.Plugins;
using RTXLens.Plugins.Msi.Configuration;
using RTXLens.Plugins.Msi.Contracts.Configuration;
using RTXLens.Plugins.Msi.Contracts.Devices;
using RTXLens.Plugins.Msi.Contracts.Telemetry;
using RTXLens.Plugins.Msi.Device;
using RTXLens.Plugins.Msi.Mapping;

namespace RTXLens.Plugins.Msi;

public sealed class MsiPlugin(PluginSettings settings, TimeProvider clock, IMsiPsuReader reader, ILogger<MsiPlugin>? logger = null) : ISensorPlugin
{
    public const string PluginId = "msi-psu";
    private MsiOptions _options = MsiSettings.GetOptions(settings);
    public PluginDescriptor Descriptor { get; private set; } = Describe(settings);
    public MsiSnapshot? Latest { get; private set; }
    public MsiDeviceInfo[] Devices => reader.Devices;

    public void ApplySettings(PluginSettings settings)
    {
        var options = MsiSettings.GetOptions(settings);
        if (_options.DevicePath != options.DevicePath || _options.FanControl != options.FanControl
            || _options.FanDutyPercent != options.FanDutyPercent || !_options.FanCurve.Matches(options.FanCurve))
        {
            reader.ReleaseFanControl();
            logger?.LogActivity(LogLevel.Information, "msi.fan-settings-changed", MsiFanControlSession.ActivitySource,
                "PSU fan settings applied.", $"Mode: {options.FanControl}.");
        }

        _options = options;
        Latest = null;
        Descriptor = Describe(settings);
    }

    public ValueTask<PluginSample> PollAsync(CancellationToken cancellationToken)
    {
        Latest = null;

        var sample = reader.Read(_options, clock.GetUtcNow(), cancellationToken);
        if (sample is null)
        {
            return ValueTask.FromResult(new PluginSample(PluginState.NoDevice, [],
                "No selected Ai1300TS or Ai1600TS is available. Select a device explicitly when more than one is connected."));
        }

        var readings = MsiSnapshotMapper.Create(sample);
        Latest = sample;

        return ValueTask.FromResult(new PluginSample(PluginState.Ready, readings, sample.FanControllerMessage is { } message
            ? "Automatic fan fallback - " + message : sample.Model + " · revision " + sample.Revision));
    }

    public void Dispose()
    {
        Latest = null;
        reader.Dispose();
    }

    private static PluginDescriptor Describe(PluginSettings settings) => new(PluginId, "MSI MPG Ai1300TS / Ai1600TS",
        TimeSpan.FromMilliseconds(settings.PollIntervalMs), TimeSpan.FromMilliseconds(settings.StaleAfterMs));
}