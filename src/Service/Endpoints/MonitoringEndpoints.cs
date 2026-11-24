using RTXLens.Contracts.Monitoring;
using RTXLens.Core.Streaming;
using System.Net;
namespace RTXLens.Service.Endpoints;

internal static class MonitoringEndpoints
{
    public static void MapMonitoringEndpoints(this WebApplication app)
    {
        app.MapHealthEndpoint().WithDescription("Returns the health status of the service and its uptime");
        app.MapSensorsEndpoint().WithDescription("Streams sensor data to the client via WebSocket");

        app.MapGet("/" + ServiceRoutes.WarningStream, async (HttpContext context, [FromKeyedServices(ServiceRoutes.WarningStream)] SensorStreamHub hub, IHostApplicationLifetime lifetime) =>
        {
            if (context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address)
                || !WarningProfileEndpoints.IsLocalClient(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await SensorWebSocket.StreamAsync(context, hub, lifetime);
        });

        app.MapVersionEndpoint();

        app.MapConfigurationEndpoints();
        app.MapWarningProfileEndpoints();
        app.MapPluginEndpoints();
        app.MapServiceLogEndpoints();

        app.MapPost("/" + ServiceRoutes.Restart, (Services.IServiceRestart restart) =>
        {
            restart.RequestRestart();
            return Results.Ok();
        });
    }
}
