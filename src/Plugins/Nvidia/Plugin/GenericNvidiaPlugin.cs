using RTXLens.Plugins.GenericNvidia.Contracts;
using RTXLens.Contracts.Configuration;
using RTXLens.Core.Plugins;
using RTXLens.Contracts.Enums;
using RTXLens.Hardware.Nvidia;
using RTXLens.Plugins.GenericNvidia.Configuration;
using RTXLens.Plugins.GenericNvidia.Contracts.Configuration;
using RTXLens.Hardware.Nvidia.Diagnostics;

namespace RTXLens.Plugins.GenericNvidia;

public sealed class GenericNvidiaPlugin(PluginSettings settings, NvidiaTelemetryReader reader, INvidiaDiagnosticsReader? diagnostics = null) : ISensorPlugin
{
    public const string PluginId = NvidiaIdentity.Id;

    private GenericNvidiaOptions _options = GenericNvidiaSettings.GetOptions(settings);

    public PluginDescriptor Descriptor { get; private set; } = Describe(settings);

    public void ApplySettings(PluginSettings settings)
    {
        reader.ClearGpu();
        _options = GenericNvidiaSettings.GetOptions(settings);
        Descriptor = Describe(settings);
    }

    public ValueTask<PluginSample> PollAsync(CancellationToken cancellationToken)
    {
        var sample = reader.ReadGpu(_options.DeviceId, Descriptor.StaleAfter, diagnostics, cancellationToken, out var readings);

        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(readings.Length == 0
            ? new PluginSample(PluginState.NoDevice, [], "Selected desktop GeForce RTX GPU or supported telemetry is unavailable.")
            : new PluginSample(PluginState.Ready, readings, sample!.Device.Name));
    }

    public void Dispose() => reader.ClearGpu();

    private static PluginDescriptor Describe(PluginSettings settings) => new(PluginId, "Generic NVIDIA GPU",
        TimeSpan.FromMilliseconds(settings.PollIntervalMs), TimeSpan.FromMilliseconds(settings.StaleAfterMs));
}
