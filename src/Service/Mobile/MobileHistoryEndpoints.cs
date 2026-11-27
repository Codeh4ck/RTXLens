using System.Text.Json;
using System.Text.Json.Serialization;
using RTXLens.Contracts.Warnings;
using RTXLens.Contracts.Errors;
using RTXLens.Contracts.Monitoring;
using RTXLens.Service.Endpoints;
using RTXLens.Service.Services;

namespace RTXLens.Service.Mobile;

internal sealed class MobileHistoryEndpoints(MobileWarningHistory warnings, SafeguardJournalService safeguard, TimeProvider clock, Func<int> warningRetention)
{
    public const int MaximumReportBytes = 512 * 1024;
    private const int PageSize = 25;

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static void MapDesktop(IEndpointRouteBuilder endpoints)
    {
        var history = endpoints.MapGroup("/" + ServiceRoutes.WarningHistory)
            .WithMetadata(new Microsoft.AspNetCore.Cors.DisableCorsAttribute());
        history.AddEndpointFilter(async (context, next) =>
        {
            if (!ServiceLogEndpoints.IsLocalClient(context.HttpContext))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        history.MapGet("", (MobileHistoryEndpoints incidents) => Results.Json(incidents.Summaries(), Json));
        history.MapDelete("", (MobileHistoryEndpoints incidents) => incidents.ClearCompleted());
        history.MapGet("/{kind}/{id:guid}", (string kind, Guid id, MobileHistoryEndpoints incidents) => incidents.Detail(kind, id));
    }

    private IResult ClearCompleted()
    {
        try
        {
            warnings.ClearCompleted();
            return Results.Json(Summaries(), Json);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Results.Json(new ErrorResponse("The service could not delete all completed warning records. Refresh to see the remaining records."),
                Json, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    public IResult List(string? before)
    {
        DateTimeOffset? timestamp = null;
        Guid id = Guid.Empty;

        string? kind = null;
        if (before is not null)
        {
            var parts = before.Split('.');

            if (parts.Length != 3 || !long.TryParse(parts[0], out var ticks) || ticks < 0
                || ticks > DateTimeOffset.MaxValue.Ticks
                || !Guid.TryParseExact(parts[1], "N", out id) || parts[2] is not ("warning" or "safeguard"))
            {
                return Results.BadRequest();
            }

            timestamp = new DateTimeOffset(ticks, TimeSpan.Zero);
            kind = parts[2];
        }

        var page = Summaries()
            .Where(record => timestamp is null || record.StartedAt < timestamp
                                               || (record.StartedAt == timestamp && (record.Id.CompareTo(id) < 0 || (record.Id == id && StringComparer.Ordinal.Compare(record.Kind, kind) < 0))))
            .OrderByDescending(record => record.StartedAt)
            .ThenByDescending(record => record.Id)
            .ThenByDescending(record => record.Kind, StringComparer.Ordinal).Take(PageSize + 1).ToArray();

        return Results.Json(new { items = page.Take(PageSize), next = page.Length > PageSize ? Cursor(page[PageSize - 1]) : null }, Json);
    }

    public IEnumerable<WarningIncident> Summaries() => warnings.Summaries(Cutoff()).Concat((safeguard.Notices ?? []).Select(notice =>
        new WarningIncident(notice.Id, "safeguard", notice.Script, "Safeguard+", notice.StartedAt, null, notice.Status)
        {
            Connector = notice.Connector
        }));

    public IResult Detail(string kind, Guid id)
    {
        var incident = kind switch
        {
            "warning" => warnings.Find(id, Cutoff()),
            "safeguard" => safeguard.ReadMobileIncident(id),
            _ => null
        };

        if (incident is null)
        {
            return Results.NotFound();
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(incident, Json);
        return bytes.Length <= MaximumReportBytes ? Results.Bytes(bytes, "application/json") : Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }

    private DateTimeOffset Cutoff() => clock.GetUtcNow().AddDays(-warningRetention());

    private static string Cursor(WarningIncident incident) => FormattableString.Invariant($"{incident.StartedAt.UtcTicks}.{incident.Id:N}.{incident.Kind}");
}
