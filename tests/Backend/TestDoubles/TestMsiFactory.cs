using RTXLens.Contracts.Discovery;
using RTXLens.Plugins.Msi.Contracts;
using RTXLens.Contracts.Configuration;
using RTXLens.Core.Plugins;
using RTXLens.Plugins.Msi;
using RTXLens.Plugins.Msi.Configuration;

namespace RTXLens.Tests.TestDoubles;

internal sealed class TestMsiFactory(MsiTestState state) : ISensorPluginFactory
{
    public PluginHardwareMetadata Hardware => MsiIdentity.Hardware;
    public string Id => MsiPlugin.PluginId;
    public string Name => "MSI MPG Ai1300TS";
    public void ValidateSettings(PluginSettings settings) => MsiSettings.GetOptions(settings);
    public ISensorPlugin Create(PluginSettings settings) => new MsiPlugin(settings, TimeProvider.System, new TestMsiReader(state));
}
