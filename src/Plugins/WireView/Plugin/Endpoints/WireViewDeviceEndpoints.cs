using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using RTXLens.Contracts.Errors;
using RTXLens.Core.Diagnostics;
using RTXLens.Plugins.WireView.Contracts.Common;
using RTXLens.Core.Plugins;
using RTXLens.Plugins.WireView.Contracts.Input;
using RTXLens.Plugins.WireView.Contracts.Output;
using RTXLens.Plugins.WireView.Services;
using RTXLens.Plugins.WireView.Serialization;

namespace RTXLens.Plugins.WireView.Endpoints;

internal static class WireViewDeviceEndpoints
{
    private const string ActivitySource = "WireView Pro II";

    private static DeviceConfig ReadWriteRequest(JsonElement body)
    {
        var context = WireViewJsonContext.Default;
        RequireCompleteObject(body, context.DeviceConfig, "device configuration");
        RequireCompleteObject(body.GetProperty("display"), context.DeviceDisplayConfig, "display settings");

        return body.Deserialize(context.DeviceConfig)
            ?? throw new ArgumentException("A complete WireView device configuration is required.");
    }

    private static void RequireCompleteObject(JsonElement value, JsonTypeInfo type, string label)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"The WireView {label} must be an object.");
        }

        var remaining = type.Properties.Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!remaining.Remove(property.Name))
            {
                throw new ArgumentException($"Unknown or duplicate WireView setting: {property.Name}.");
            }
        }

        if (remaining.Count != 0)
        {
            throw new ArgumentException($"The WireView {label} is missing settings. Refresh before applying.");
        }
    }

    public static void MapWireViewDeviceEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/device/config", async (PluginRuntimeManager runtime, ILogger<WireViewPlugin> logger, CancellationToken token) =>
        {
            var configuration = await WireViewAccess.ExecuteAsync(runtime, plugin => plugin.Reader.ReadConfig(), token);

            if (configuration is null)
            {
                logger.LogActivity(LogLevel.Warning, "wireview.config-read-failed", ActivitySource, "Could not read device settings.");
            }
            else
            {
                logger.LogActivity(LogLevel.Information, "wireview.config-read", ActivitySource, "Device settings read.");
            }

            return configuration is not null
                ? Results.Json(configuration, WireViewJsonContext.Default.DeviceConfig)
                : throw new IOException("Failed to read WireView device configuration.");
        });

        group.MapPut("/device/config", async (JsonElement body, HttpContext context, PluginRuntimeManager runtime,
            ILogger<WireViewPlugin> logger, CancellationToken token) =>
        {
            var host = context.Request.Host.Host.Trim('[', ']');
            if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote)
                || context.Request.Headers.ContainsKey("Origin") || context.Request.Headers.ContainsKey("Sec-Fetch-Site")
                || !(host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                    || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address)))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            try
            {
                var changes = ReadWriteRequest(body);
                var applied = await WireViewAccess.ExecuteAsync(runtime, plugin => plugin.Reader.ApplyConfig(changes), token);
                logger.LogActivity(LogLevel.Information, "wireview.config-applied", ActivitySource, "Device settings saved and verified.");
                return Results.Json(applied, WireViewJsonContext.Default.DeviceConfig);
            }
            catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException)
            {
                logger.LogActivity(LogLevel.Warning, "wireview.config-write-unverified", ActivitySource,
                    "Could not verify the device settings write. Refresh the device settings before retrying.", exception: error);
                return Results.Json(new ErrorResponse("Could not verify the WireView settings. Refresh before trying again."),
                    WireViewJsonContext.Default.ErrorResponse, statusCode: StatusCodes.Status502BadGateway);
            }
        }).WithMetadata(new DisableCorsAttribute());

        group.MapGet("/device/config/raw", async (PluginRuntimeManager runtime, CancellationToken token) =>
        {
            var bytes = await WireViewAccess.ExecuteAsync(runtime, plugin => plugin.Reader.ReadConfigRaw(), token);

            return bytes is not null
                ? Results.Json(new RawConfigResponse(BitConverter.ToString(bytes), bytes.Length), WireViewJsonContext.Default.RawConfigResponse)
                : throw new IOException("Failed to read raw WireView device configuration.");
        });

        group.MapPost("/faults/clear", async (ClearFaultsRequest request, PluginRuntimeManager runtime, ILogger<WireViewPlugin> logger, CancellationToken token) =>
        {
            var statusMask = (ushort)~request.FaultStatusMask;
            var logMask = (ushort)~request.FaultLogMask;
            var sent = await WireViewAccess.ExecuteAsync(runtime, plugin => plugin.Reader.ClearFaults(statusMask, logMask), token);

            if (sent)
            {
                logger.LogActivity(LogLevel.Information, "wireview.fault-clear-sent", ActivitySource, "Fault-clear command sent to the device.");
            }
            else
            {
                logger.LogActivity(LogLevel.Error, "wireview.fault-clear-failed", ActivitySource,
                    "Could not send the device fault-clear command.", eventViewer: true);
            }

            return sent ? Results.Ok() : throw new IOException("Failed to send the WireView fault-clear command.");
        });
    }
}