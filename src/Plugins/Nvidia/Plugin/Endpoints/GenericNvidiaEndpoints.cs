using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RTXLens.Core.Configuration;
using RTXLens.Core.Http;
using RTXLens.Core.Plugins;
using RTXLens.Hardware.Nvidia;
using RTXLens.Plugins.GenericNvidia.Contracts.Devices;
using RTXLens.Plugins.GenericNvidia.Serialization;

namespace RTXLens.Plugins.GenericNvidia.Endpoints;

public static class GenericNvidiaEndpoints
{
    public static void MapGenericNvidiaEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/plugins/generic-nvidia").AddEndpointFilter<ApiExceptionFilter>();

        group.MapGet("/devices", async (NvidiaTelemetryReader reader, bool? refresh, CancellationToken token) =>
        {
            var devices = await Task.Run(() => reader.GetDevices(refresh: refresh != false), token);
            return Results.Json([.. devices.Select(device => new NvidiaDeviceInfo(device.Id, device.Name, device.LegacyId))],
                GenericNvidiaJsonContext.Default.NvidiaDeviceInfoArray);
        });

        group.MapGet("/config", (IRuntimeConfiguration configuration) => Results.Json(
            configuration.Current.Configuration.Plugins[GenericNvidiaPlugin.PluginId], GenericNvidiaJsonContext.Default.PluginSettings));

        group.MapPost("/restart", async (PluginRuntimeManager runtime, CancellationToken token) =>
        {
            await runtime.RestartAsync(GenericNvidiaPlugin.PluginId, token);
            return Results.Ok();
        });
    }
}
