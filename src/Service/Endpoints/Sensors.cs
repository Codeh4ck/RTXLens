using RTXLens.Contracts.Monitoring;
using RTXLens.Core.Streaming;

namespace RTXLens.Service.Endpoints;

internal static class Sensors
{
    public static RouteHandlerBuilder MapSensorsEndpoint(this IEndpointRouteBuilder endpoints, Func<byte[], byte[]>? formatFrame = null) =>
        endpoints.MapGet("/" + ServiceRoutes.Sensors, (HttpContext context, SensorStreamHub hub, IHostApplicationLifetime lifetime) =>
            SensorWebSocket.StreamAsync(context, hub, lifetime, formatFrame));
}
