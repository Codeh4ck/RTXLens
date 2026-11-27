using RTXLens.Contracts.Monitoring;
using System.Text.Json;
using System.Net;
using RTXLens.Contracts.Configuration;
using RTXLens.Core.Configuration;
using RTXLens.Core.Http;
using RTXLens.Service.Configuration;
using RTXLens.Service.Services;

namespace RTXLens.Service.Endpoints;

internal static class WarningProfileEndpoints
{
    public static void MapWarningProfileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var profiles = endpoints.MapGroup("/" + ServiceRoutes.WarningProfiles);

        profiles.MapGet("", (DeviceWarningProfileStore store, SettingsProfileResolver settings, IRuntimeConfiguration config, WarningPowerService power) =>
        {
            var catalog = store.Catalog(settings.Automatic, config.Current.Configuration);
            return Results.Json(catalog with { PowerTargets = power.Targets(), HeldLimits = power.Held }, AppJsonContext.Default.DeviceWarningProfileCatalog);
        });

        profiles.MapPut("", (JsonElement profile, HttpContext context, DeviceWarningProfileStore store, WarningPowerService power,
            SettingsProfileResolver settings, IRuntimeConfiguration config) =>
        {
            if (!IsLocalClient(context.Request))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var draft = store.ReadProfile(profile);
            if (draft.PowerReduction is { } reaction)
            {
                var previous = store.Catalog(settings.Automatic, config.Current.Configuration).Profiles.FirstOrDefault(p => p.Id == draft.Id);
                if (previous?.PowerReduction != reaction || previous.PluginId != draft.PluginId || previous.DeviceId != draft.DeviceId || (!previous.Enabled && draft.Enabled))
                {
                    var target = power.Targets().FirstOrDefault(gpu => gpu.Id == reaction.GpuId);
                    if (target is null || reaction.TargetMilliwatts < target.Minimum || reaction.TargetMilliwatts > target.Maximum)
                    {
                        throw new ArgumentException("Choose a reporting GPU and a limit inside its driver range.");
                    }

                    if (draft.PluginId == Plugins.Asus.Contracts.AsusIdentity.Id && draft.DeviceId is not null && draft.DeviceId != target.Id)
                    {
                        throw new ArgumentException("An Astral warning must reduce power on the same GPU.");
                    }
                }
            }
            store.Save(draft);
            return Results.NoContent();
        }).AddEndpointFilter<ApiExceptionFilter>();

        profiles.MapPost("/debug-power", (DebugPowerFrame frame, HttpContext context, DebugPowerSimulation simulation) =>
        {
            if (!IsLocalClient(context.Request) || context.Connection.RemoteIpAddress is { } remote && !IPAddress.IsLoopback(remote))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            simulation.Receive(frame);
            return Results.NoContent();
        }).AddEndpointFilter<ApiExceptionFilter>();

        profiles.MapPost("/restore-power", (string gpuId, HttpContext context, WarningPowerService power) =>
        {
            if (!IsLocalClient(context.Request))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            power.RequestRestore(gpuId);
            return Results.NoContent();
        }).AddEndpointFilter<ApiExceptionFilter>();

        profiles.MapDelete("/{id}", (string id, HttpContext context, DeviceWarningProfileStore store) =>
        {
            if (!IsLocalClient(context.Request))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            store.Delete(id);
            return Results.NoContent();
        }).AddEndpointFilter<ApiExceptionFilter>();

        endpoints.MapGet("/" + ServiceRoutes.WarningShutdown, (HttpContext context, WarningShutdownCoordinator shutdown) =>
        {
            if (!IsLocalClient(context.Request))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var reason = shutdown.ReadAfterBoot();
            return reason is null ? Results.NoContent() : Results.Json(reason, AppJsonContext.Default.WarningShutdownReason);
        });

        endpoints.MapDelete("/" + ServiceRoutes.WarningShutdown + "/{id}", (string id, HttpContext context, WarningShutdownCoordinator shutdown) =>
        {
            if (!IsLocalClient(context.Request))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            shutdown.Acknowledge(id);
            return Results.NoContent();
        }).AddEndpointFilter<ApiExceptionFilter>();
    }

    internal static bool IsLocalClient(HttpRequest request)
    {
        var host = request.Host.Host.Trim('[', ']');
        return !request.Headers.ContainsKey("Origin") && !request.Headers.ContainsKey("Sec-Fetch-Site")
            && (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
    }
}
