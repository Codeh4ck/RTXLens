using RTXLens.Contracts.Discovery;
using RTXLens.Contracts.Configuration;
using RTXLens.Core.Plugins;

namespace RTXLens.Tests.TestDoubles;

internal sealed class TestSensorPluginFactory(string id, TestPluginBehavior behavior = TestPluginBehavior.PublishReadings) : ISensorPluginFactory
{
    public string Id => id;
    public string Name => id;
    public PluginHardwareMetadata Hardware { get; init; } = new(id);

    public ISensorPlugin Create(PluginSettings settings)
    {
        var plugin = new TestSensorPlugin(id, behavior);
        plugin.ApplySettings(settings);
        return plugin;
    }
}
