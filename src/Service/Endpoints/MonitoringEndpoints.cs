namespace RTXLens.Service.Endpoints;

internal static class MonitoringEndpoints
{
    public static void MapMonitoringEndpoints(this WebApplication app)
    {
        app.MapHealthEndpoint().WithDescription("Returns the health status of the service and its uptime");
        app.MapSensorsEndpoint().WithDescription("Streams sensor data to the client via WebSocket");

        app.MapVersionEndpoint();

        app.MapConfigurationEndpoints();
        app.MapWarningProfileEndpoints();
        app.MapPluginEndpoints();
        app.MapServiceLogEndpoints();

        app.MapPost("/restart", (Services.IServiceRestart restart) =>
        {
            restart.RequestRestart();
            return Results.Ok();
        });
    }
}
