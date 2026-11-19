using RTXLens.Contracts.Discovery;
using RTXLens.Core;
using RTXLens.Core.Configuration;
using RTXLens.Core.Plugins;

namespace RTXLens.Service.Endpoints;

internal static class PluginEndpoints
{
    public static void MapPluginEndpoints(this IEndpointRouteBuilder endpoints) => endpoints.MapGet("/plugins",
        (PluginRuntimeManager runtime, SensorStateStore store, IRuntimeConfiguration configuration, TimeProvider clock) =>
        {
            var health = store.GetPluginHealth(clock.GetUtcNow()).ToDictionary(plugin => plugin.PluginId);
            var settings = configuration.Current.Configuration.Plugins;

            var plugins = runtime.Definitions.Select(definition =>
            {
                var state = health[definition.Id];
                return new PluginInfo(definition.Id, definition.Name, settings[definition.Id].Enabled,
                    state.State, state.LastSuccessAt, state.Message, definition.Capabilities) { Warnings = definition.Warnings, ConnectorMetrics = definition.ConnectorMetrics };
            }).ToArray();

            return Results.Json(new PluginCatalog(1, plugins), AppJsonContext.Default.PluginCatalog);
        });
}
