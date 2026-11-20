using Microsoft.Extensions.DependencyInjection;
using RTXLens.Core.Plugins;
using RTXLens.Plugins.Asus;
using RTXLens.Plugins.GenericNvidia;
using RTXLens.Plugins.Msi;
using RTXLens.Plugins.WireView;

namespace RTXLens.Tests.Infrastructure;

internal static class TestHardwareCatalog
{
    public static ServiceProvider Services { get; } = new ServiceCollection().AddLogging().AddSingleton(TimeProvider.System)
        .AddAsusPlugin().AddGenericNvidiaPlugin().AddMsiPlugin().AddWireViewPlugin().BuildServiceProvider();

    public static HardwareCatalog Installed { get; } = new(Services.GetServices<ISensorPluginFactory>());
}
