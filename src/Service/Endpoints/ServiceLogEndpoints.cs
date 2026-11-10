using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Http.Features;
using RTXLens.Service.Diagnostics;

namespace RTXLens.Service.Endpoints;

internal static class ServiceLogEndpoints
{
    public static void MapServiceLogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/logs").WithMetadata(new DisableCorsAttribute());
        group.MapGet("", (HttpContext context, ServiceLogJournal journal, long? before, int? limit) =>
        {
            if (!IsLocalClient(context))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(journal.ReadHistory(Math.Max(0, before ?? 0), Math.Clamp(limit ?? 250, 1, 250)),
                AppJsonContext.Default.ServiceLogPage);
        });

        group.MapDelete("", (HttpContext context, ServiceLogJournal journal) =>
        {
            if (!IsLocalClient(context))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            context.Response.Headers.CacheControl = "no-store";

            try
            {
                return Results.Json(journal.Clear(), AppJsonContext.Default.ServiceLogPage);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return Results.Problem("The backend could not delete the saved event history.");
            }
        });

        group.MapGet("/stream", StreamAsync);
    }

    private static async Task StreamAsync(HttpContext context, ServiceLogJournal journal, IHostApplicationLifetime lifetime,
        string? instance, long? after)
    {
        if (!IsLocalClient(context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
        var response = context.Response;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-store";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var sequence = Math.Max(0, after ?? 0);
        long first = 0;

        try
        {
            while (!stop.IsCancellationRequested)
            {
                var (page, changed) = journal.ReadAfter(sequence);
                if (instance != page.InstanceId || sequence > page.LatestSequence)
                {
                    instance = page.InstanceId;
                    sequence = Math.Max(0, page.FirstSequence - 1);
                    first = 0;
                    (page, changed) = journal.ReadAfter(sequence);
                }

                if (first != page.FirstSequence || page.Entries.Length > 0)
                {
                    first = page.FirstSequence;
                    sequence = page.Entries.Length == 0 ? page.LatestSequence : page.Entries[^1].Sequence;

                    var json = JsonSerializer.Serialize(page, AppJsonContext.Default.ServiceLogPage);
                    await WriteAsync(response, $"data: {json}\n\n", stop.Token);
                    if (sequence < page.LatestSequence)
                    {
                        continue;
                    }
                }

                try
                {
                    await changed.WaitAsync(TimeSpan.FromSeconds(15), stop.Token);
                }
                catch (TimeoutException)
                {
                    await WriteAsync(response, ": heartbeat\n\n", stop.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static async Task WriteAsync(HttpResponse response, string text, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await response.WriteAsync(text, deadline.Token);
        await response.Body.FlushAsync(deadline.Token);
    }

    private static bool IsLocalClient(HttpContext context)
    {
        var request = context.Request;
        var host = request.Host.Host.Trim('[', ']');

        return context.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote)
            && !request.Headers.ContainsKey("Origin") && !request.Headers.ContainsKey("Sec-Fetch-Site")
            && (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
    }
}
