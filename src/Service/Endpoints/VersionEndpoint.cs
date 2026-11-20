using RTXLens.Contracts.Monitoring;
using System.Reflection;
using RTXLens.Contracts.Updates;

namespace RTXLens.Service.Endpoints;

internal static class VersionEndpoint
{
    public static void MapVersionEndpoint(this IEndpointRouteBuilder app) =>
        app.MapGet("/" + ServiceRoutes.Version, () =>
        {
            var assembly = typeof(VersionEndpoint).Assembly;
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
                          ?? assembly.GetName().Version?.ToString(3) ?? "unknown";

            return Results.Json(new ServiceVersionResponse(1, version), AppJsonContext.Default.ServiceVersionResponse);
        });
}
