using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Core.Plugins;
using RTXLens.Contracts.Configuration;

namespace RTXLens.Tests.TestDoubles;

internal sealed class TestSensorPlugin(string pluginId, TestPluginBehavior behavior = TestPluginBehavior.PublishReadings, Action? beforePoll = null) : ISensorPlugin
{
    public PluginDescriptor Descriptor { get; private set; } = new(pluginId, pluginId, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5));

    public void ApplySettings(PluginSettings settings) =>
        Descriptor = Descriptor with
        {
            PollInterval = TimeSpan.FromMilliseconds(settings.PollIntervalMs),
            StaleAfter = TimeSpan.FromMilliseconds(settings.StaleAfterMs)
        };

    public async ValueTask<PluginSample> PollAsync(CancellationToken cancellationToken)
    {
        beforePoll?.Invoke();
        switch (behavior)
        {
            case TestPluginBehavior.WaitForCancellation:
                await Task.Delay(Timeout.Infinite, cancellationToken);
                break;
            case TestPluginBehavior.ThrowOnPoll:
                throw new InvalidOperationException("Simulated provider failure.");
        }

        var identity = new SensorIdentity(Descriptor.Id, "device", SensorComponent.GpuConnector, SensorMetric.Power, Channel: 1);
        var reading = new SensorReading(identity, SensorUnit.Watts, 400, DateTimeOffset.UtcNow);

        return new PluginSample(PluginState.Ready, [reading]);
    }

    public void Dispose() { }
}
