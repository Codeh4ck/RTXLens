using RTXLens.Core.Plugins;

namespace RTXLens.Plugins.WireView.Services;

internal static class WireViewAccess
{
    public static Task<T> ExecuteAsync<T>(PluginRuntimeManager runtime, Func<WireViewPlugin, T> operation, CancellationToken token) =>
        runtime.ExecuteAsync(WireViewPlugin.PluginId, plugin =>
        {
            var wireView = (WireViewPlugin)plugin;
            return wireView.Latest is null ? throw new PluginUnavailableException("WireView Pro II device is either not available or not connected.") : operation(wireView);
        }, token);
}