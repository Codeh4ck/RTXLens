using RTXLens.Contracts.Configuration;
using RTXLens.Core.Plugins;
using RTXLens.Contracts.Enums;
using RTXLens.Hardware.Nvidia;
using RTXLens.Plugins.GenericNvidia.Configuration;
using RTXLens.Plugins.GenericNvidia.Contracts.Configuration;
using RTXLens.Plugins.GenericNvidia.Diagnostics;
using RTXLens.Plugins.GenericNvidia.Mapping;

namespace RTXLens.Plugins.GenericNvidia;

public sealed class GenericNvidiaPlugin(PluginSettings settings, NvidiaTelemetryReader reader, INvidiaDiagnosticsReader? diagnostics = null) : ISensorPlugin
{
    public const string PluginId = "generic-nvidia";

    private GenericNvidiaOptions _options = GenericNvidiaSettings.GetOptions(settings);
    private int _pollIntervalMs = settings.PollIntervalMs;

    public PluginDescriptor Descriptor { get; private set; } = Describe(settings);

    public void ApplySettings(PluginSettings settings)
    {
        _options = GenericNvidiaSettings.GetOptions(settings);
        _pollIntervalMs = settings.PollIntervalMs;
        Descriptor = Describe(settings);
    }

    public ValueTask<PluginSample> PollAsync(CancellationToken cancellationToken)
    {
        var sample = reader.Read(_options.DeviceId, _pollIntervalMs, cancellationToken);
        var readings = sample is null ? [] : GenericNvidiaSnapshotMapper.Create(sample);

        if (sample is not null && diagnostics?.Read(sample.Device.Id) is { } diagnosticSample)
        {
            cancellationToken.ThrowIfCancellationRequested();
            readings = [.. readings, .. NvidiaDiagnosticsMapper.Create(diagnosticSample)];
        }

        return ValueTask.FromResult(readings.Length == 0
            ? new PluginSample(PluginState.NoDevice, [], "Selected desktop GeForce RTX GPU or supported telemetry is unavailable.")
            : new PluginSample(PluginState.Ready, readings, sample!.Device.Name));
    }

    public void Dispose()
    {
        // The host owns the reader shared with the ASUS plugin.
    }

    private static PluginDescriptor Describe(PluginSettings settings) => new(PluginId, "Generic NVIDIA GPU",
        TimeSpan.FromMilliseconds(settings.PollIntervalMs), TimeSpan.FromMilliseconds(settings.StaleAfterMs));
}
