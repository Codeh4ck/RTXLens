using RTXLens.Contracts.Discovery;
using RTXLens.Plugins.WireView.Contracts;
using RTXLens.Plugins.WireView.Analysis.Warnings;
using RTXLens.Warnings;
using RTXLens.Contracts.Configuration;
using RTXLens.Core.Plugins;
using RTXLens.Plugins.WireView;
using RTXLens.Plugins.WireView.Streaming;

namespace RTXLens.Tests.TestDoubles;

internal sealed class TestWireViewFactory(WireViewTestState state, WireViewStream stream) : ISensorPluginFactory
{
    public IWarningSource WarningSource { get; } = new WireViewWarningSource();
    public PluginHardwareMetadata Hardware => WireViewIdentity.Hardware;
    public string Id => WireViewPlugin.PluginId;
    public string Name => "Test WireView";

    public ISensorPlugin Create(PluginSettings settings)
    {
        if (state.FailCreation)
        {
            throw new InvalidOperationException("Simulated plugin activation failure.");
        }

        Interlocked.Increment(ref state.CreateCount);
        return new WireViewPlugin(settings, TimeProvider.System, new TestWireViewReader(state), stream);
    }
}
