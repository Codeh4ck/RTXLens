using RTXLens.Contracts.Discovery;
using RTXLens.Plugins.Asus.Contracts;
using RTXLens.Plugins.Asus.Analysis.Warnings;
using RTXLens.Warnings;
using RTXLens.Plugins.Asus.Contracts.Configuration;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using RTXLens.Contracts.Configuration;
using RTXLens.Core.Plugins;
using RTXLens.Plugins.Asus;
using RTXLens.Plugins.Asus.Configuration;
using RTXLens.Plugins.Asus.Serialization;
using RTXLens.Plugins.Asus.Streaming;

namespace RTXLens.Tests.TestDoubles;

internal sealed class TestAsusFactory(AsusTestState state, AsusStream stream) : ISensorPluginFactory
{
    public IWarningSource WarningSource { get; } = new AsusWarningSource();
    public PluginHardwareMetadata Hardware => AsusIdentity.Hardware;
    public string Id => AsusPlugin.PluginId;
    public string Name => "Test ASUS GPU";

    public PluginSettings CreateDefaultSettings() => AsusSettings.WithOptions(new PluginSettings(), new AsusOptions());
    public void ValidateSettings(PluginSettings settings) => AsusSettings.GetOptions(settings);
    public JsonElement ReadBootstrapOptions(IConfigurationSection section) =>
        JsonSerializer.SerializeToElement(section.Get<AsusOptions>(binding => binding.ErrorOnUnknownConfiguration = true)
            ?? new AsusOptions(), AsusJsonContext.Default.AsusOptions);

    public ISensorPlugin Create(PluginSettings settings)
    {
        Interlocked.Increment(ref state.CreateCount);
        return new AsusPlugin(settings, TimeProvider.System, new TestAsusReader(state), stream);
    }
}
