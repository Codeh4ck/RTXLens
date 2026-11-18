using System.Text.Json;
using System.Text.Json.Serialization;
using RTXLens.Contracts.Sensors;
using RTXLens.Plugins.Msi.Contracts.Safeguard;
using RTXLens.Service.Services;

namespace RTXLens.Service.Mobile;

internal sealed record MobileIncident(Guid Id, string Kind, string Title, string Source, DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt, string Status)
{
    public string? Detail { get; init; }
    public string? DeviceId { get; init; }
    public int? Connector { get; init; }
    public int? DelayMs { get; init; }
    public string? DeliveryStatus { get; init; }
    public DateTimeOffset? CapturedAt { get; init; }
    public SensorReading[] Readings { get; init; } = [];
    public SafeguardStep[] Steps { get; init; } = [];
}

internal sealed class MobileHistoryEndpoints(MobileWarningHistory warnings, SafeguardJournalService safeguard, TimeProvider clock,
    Func<int> warningRetention)
{
    public const int MaximumReportBytes = 512 * 1024;
    private const int PageSize = 25;

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public IResult List(string? before)
    {
        DateTimeOffset? timestamp = null;
        Guid id = default;
        string? kind = null;
        if (before is not null)
        {
            var parts = before.Split('.');
            if (parts.Length != 3 || !long.TryParse(parts[0], out var ticks) || ticks < 0 || ticks > DateTimeOffset.MaxValue.Ticks
                || !Guid.TryParseExact(parts[1], "N", out id) || parts[2] is not ("warning" or "safeguard"))
            {
                return Results.BadRequest();
            }

            timestamp = new DateTimeOffset(ticks, TimeSpan.Zero);
            kind = parts[2];
        }

        var incidents = warnings.Summaries(Cutoff()).Concat((safeguard.Notices ?? []).Select(notice =>
            new MobileIncident(notice.Id, "safeguard", notice.Script, "Safeguard+", notice.StartedAt, null, notice.Status)
            {
                Connector = notice.Connector
            }));

        var page = incidents.Where(record => timestamp is null || record.StartedAt < timestamp
            || (record.StartedAt == timestamp && (record.Id.CompareTo(id) < 0
                || (record.Id == id && StringComparer.Ordinal.Compare(record.Kind, kind) < 0))))
            .OrderByDescending(record => record.StartedAt).ThenByDescending(record => record.Id)
            .ThenByDescending(record => record.Kind, StringComparer.Ordinal).Take(PageSize + 1).ToArray();

        return Results.Json(new { items = page.Take(PageSize), next = page.Length > PageSize ? Cursor(page[PageSize - 1]) : null }, Json);
    }

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
        return bytes.Length <= MaximumReportBytes ? Results.Bytes(bytes, "application/json")
            : Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }

    private DateTimeOffset Cutoff() => clock.GetUtcNow().AddDays(-warningRetention());

    private static string Cursor(MobileIncident incident) => FormattableString.Invariant(
        $"{incident.StartedAt.UtcTicks}.{incident.Id:N}.{incident.Kind}");
}
