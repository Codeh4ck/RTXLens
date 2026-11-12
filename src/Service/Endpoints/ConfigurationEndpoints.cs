using RTXLens.Core.Configuration;
using RTXLens.Contracts.Configuration;
using RTXLens.Core.Http;
using RTXLens.Service.Configuration;

namespace RTXLens.Service.Endpoints;

internal static class ConfigurationEndpoints
{
    public static void MapConfigurationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/settings-profiles", (SettingsProfileResolver profiles) =>
            Results.Json(profiles.Catalog(), AppJsonContext.Default.SettingsProfileCatalog));

        endpoints.MapPut("/settings-profiles", (SettingsProfileRequest request, SettingsProfileStore store, SettingsProfileResolver profiles) =>
        {
            store.Apply(request);
            return Results.Json(profiles.Catalog(), AppJsonContext.Default.SettingsProfileCatalog);
        }).AddEndpointFilter<ApiExceptionFilter>();

        endpoints.MapPut("/settings-profiles/{id}", (string id, SettingsProfile settings, HttpContext context,
            SettingsProfileStore store, SettingsProfileResolver profiles) =>
        {
            if (!WarningProfileEndpoints.IsLocalClient(context.Request))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            store.Update(id, settings);
            return Results.Json(profiles.Catalog(), AppJsonContext.Default.SettingsProfileCatalog);
        }).AddEndpointFilter<ApiExceptionFilter>();

        endpoints.MapDelete("/settings-profiles/{id}", (string id, HttpContext context,
            SettingsProfileStore store, SettingsProfileResolver profiles) =>
        {
            if (!WarningProfileEndpoints.IsLocalClient(context.Request))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            store.Delete(id);
            return Results.Json(profiles.Catalog(), AppJsonContext.Default.SettingsProfileCatalog);
        }).AddEndpointFilter<ApiExceptionFilter>();

        endpoints.MapGet("/configuration", (HttpContext context, IRuntimeConfiguration configuration) => ConfigurationPrecondition.Respond(context, configuration.Current));

        endpoints.MapPut("/configuration",
            async (ServiceConfiguration settings, HttpContext context, IRuntimeConfiguration configuration, CancellationToken token) =>
            {
                var updated = await configuration.UpdateAsync(_ => settings, token, ConfigurationPrecondition.Read(context.Request));
                return ConfigurationPrecondition.Respond(context, updated);
            }).AddEndpointFilter<ApiExceptionFilter>();

        endpoints.MapGet("/plugins/{pluginId}/settings", (string pluginId, IRuntimeConfiguration configuration) =>
            Results.Ok(configuration.Current.Configuration.Plugins[pluginId])).AddEndpointFilter<ApiExceptionFilter>();

        endpoints.MapPut("/plugins/{pluginId}/settings", UpdatePluginSettingsAsync).AddEndpointFilter<ApiExceptionFilter>();
    }

    private static async Task<IResult> UpdatePluginSettingsAsync(string pluginId, PluginSettings settings, HttpContext context,
        IRuntimeConfiguration configuration, CancellationToken cancellationToken)
    {
        var updated = await configuration.UpdateAsync(current =>
        {
            if (!current.Plugins.ContainsKey(pluginId))
            {
                throw new KeyNotFoundException($"Plugin '{pluginId}' is not installed.");
            }

            current.Plugins[pluginId] = settings;

            return current;
        }, cancellationToken, ConfigurationPrecondition.Read(context.Request));

        return ConfigurationPrecondition.Respond(context, updated);
    }
}
