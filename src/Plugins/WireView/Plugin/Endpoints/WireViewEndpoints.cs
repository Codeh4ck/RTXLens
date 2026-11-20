using RTXLens.Plugins.WireView.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RTXLens.Contracts.Enums;
using RTXLens.Core;
using RTXLens.Core.Http;
using RTXLens.Core.Plugins;
using RTXLens.Plugins.WireView.Contracts.Output;
using RTXLens.Plugins.WireView.Serialization;

namespace RTXLens.Plugins.WireView.Endpoints;

public static class WireViewEndpoints
{
    public static void MapWireViewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/" + WireViewIdentity.Route)
            .AddEndpointFilter<ApiExceptionFilter>();

        group.MapGet("/health", (SensorStateStore store, TimeProvider clock) =>
        {
            var health = store.Snapshot(clock.GetUtcNow(), []).Plugins.Single(plugin => plugin.PluginId == WireViewPlugin.PluginId);
            var status = health.State switch
            {
                PluginState.Ready => "ok",
                PluginState.Disabled => "disabled",
                _ => "no_device"
            };

            return Results.Json(new HealthResponse { Status = status }, WireViewJsonContext.Default.HealthResponse);
        });

        group.MapWireViewConfigEndpoints();
        group.MapWireViewSensorEndpoints();
        group.MapWireViewDeviceEndpoints();

        group.MapPost("/restart", async (PluginRuntimeManager runtime, CancellationToken token) =>
        {
            await runtime.RestartAsync(WireViewPlugin.PluginId, token);
            return Results.Ok();
        });
    }
}