using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RTXLens.Core.Http;
using RTXLens.Core.Plugins;
using RTXLens.Plugins.Msi.Serialization;
using RTXLens.Plugins.Msi.Device;
using RTXLens.Plugins.Msi.Contracts.Devices;

namespace RTXLens.Plugins.Msi.Endpoints;

public static class MsiEndpoints
{
    public static void MapMsiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/plugins/msi-psu").AddEndpointFilter<ApiExceptionFilter>();
        group.MapPost("/restart", async (PluginRuntimeManager runtime, CancellationToken token) =>
        {
            await runtime.RestartAsync(MsiPlugin.PluginId, token);
            return Results.Ok();
        });

        group.MapGet("/device", async (PluginRuntimeManager runtime, CancellationToken token) =>
        {
            var snapshot = await runtime.ExecuteAsync(MsiPlugin.PluginId, plugin =>
                ((MsiPlugin)plugin).Latest ?? throw new PluginUnavailableException("No current MSI PSU snapshot is available."), token);
            return Results.Json(snapshot, MsiJsonContext.Default.MsiSnapshot);
        });

        group.MapGet("/devices", async (PluginRuntimeManager runtime, CancellationToken token) =>
        {
            MsiDeviceInfo[] devices;
            try
            {
                devices = await runtime.ExecuteAsync(MsiPlugin.PluginId, plugin => ((MsiPlugin)plugin).Devices, token);
            }
            catch (PluginUnavailableException)
            {
                devices = await Task.Run(MsiPsuReader.DiscoverDevices, token);
            }

            return Results.Json(devices, MsiJsonContext.Default.MsiDeviceInfoArray);
        });
    }
}