using RTXLens.Core.Configuration;
using RTXLens.Contracts.Health;
using RTXLens.Core;
using RTXLens.Service.Utilities;

namespace RTXLens.Service.Endpoints;

internal static class Health
{
    public static RouteHandlerBuilder MapHealthEndpoint(this IEndpointRouteBuilder app) =>
        app.MapGet("/health", (SensorStateStore store, MonitoringServiceLifetime lifetime, TimeProvider clock, IRuntimeConfiguration configuration) =>
        {
            var now = clock.GetUtcNow();
            var current = configuration.Current;

            return Results.Json(new HealthResponse("running", lifetime.StartedAt,
                    Math.Max(0, (now - lifetime.StartedAt).TotalSeconds), store.GetPluginHealth(now),
                    current.Revision, current.LastError),
                AppJsonContext.Default.HealthResponse);
        });
}
