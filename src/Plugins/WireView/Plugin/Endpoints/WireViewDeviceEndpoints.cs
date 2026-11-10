using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using RTXLens.Core.Diagnostics;
using RTXLens.Core.Plugins;
using RTXLens.Plugins.WireView.Contracts.Input;
using RTXLens.Plugins.WireView.Contracts.Output;
using RTXLens.Plugins.WireView.Services;
using RTXLens.Plugins.WireView.Serialization;

namespace RTXLens.Plugins.WireView.Endpoints;

internal static class WireViewDeviceEndpoints
{
    private const string ActivitySource = "WireView Pro II";

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