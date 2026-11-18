using RTXLens.Core.Streaming;

namespace RTXLens.Service.Endpoints;

internal static class Sensors
{
    public static RouteHandlerBuilder MapSensorsEndpoint(this IEndpointRouteBuilder endpoints, Func<byte[], byte[]>? formatFrame = null) =>
        endpoints.MapGet("/sensors", (HttpContext context, SensorStreamHub hub, IHostApplicationLifetime lifetime) =>
            SensorWebSocket.StreamAsync(context, hub, lifetime, formatFrame));
}
