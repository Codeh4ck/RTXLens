using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RTXLens.Service.Mobile;

internal sealed partial class ExpoMobilePushProvider(HttpClient client)
{
    private static readonly Uri SendUri = new("https://exp.host/--/api/v2/push/send");
    private static readonly Uri ReceiptsUri = new("https://exp.host/--/api/v2/push/getReceipts");

    public static bool IsToken(string? token) => token is { Length: >= 20 and <= 160 } && ExpoTokenPattern().IsMatch(token);

    [GeneratedRegex(@"\A(?:ExpoPushToken|ExponentPushToken)\[[A-Za-z0-9_-]{1,140}\]\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExpoTokenPattern();

    public Task<MobilePushResult> SendAsync(string token, MobilePushAlert alert, CancellationToken cancellationToken) =>
        PostAsync(SendUri, new
        {
            to = token,
            title = alert.Title,
            body = alert.IncidentId is null ? "Open RTX Lens to view warnings." : "Open RTX Lens to view the incident.",
            sound = "default",
            priority = "high",
            channelId = "rtxlens-warnings",
            ttl = 300,
            data = new { screen = "warnings", incidentId = alert.IncidentId, kind = alert.IncidentKind }
        }, null, cancellationToken);

    public Task<MobilePushResult> CheckAsync(string ticketId, CancellationToken cancellationToken) =>
        PostAsync(ReceiptsUri, new { ids = new[] { ticketId } }, ticketId, cancellationToken);

    private async Task<MobilePushResult> PostAsync(Uri uri, object payload, string? ticketId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.PostAsJsonAsync(uri, payload, cancellationToken);

            if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                return new(MobilePushOutcome.Retry);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new(MobilePushOutcome.Rejected);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("data", out var data))
            {
                return new(MobilePushOutcome.Rejected);
            }

            if (ticketId is not null && (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(ticketId, out data)))
            {
                return new(MobilePushOutcome.Pending);
            }

            return ReadResult(data);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new(MobilePushOutcome.Retry);
        }
        catch (JsonException)
        {
            return new(MobilePushOutcome.Rejected);
        }
    }

    private static MobilePushResult ReadResult(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String)
        {
            return new(MobilePushOutcome.Rejected);
        }

        if (status.GetString() == "ok")
        {
            var ticketId = data.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
            return new(MobilePushOutcome.Accepted, ticketId is { Length: > 0 and <= 128 } ? ticketId : null);
        }

        var code = data.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Object
            && details.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : null;

        return code switch
        {
            "DeviceNotRegistered" => new(MobilePushOutcome.Unregistered),
            "MessageRateExceeded" => new(MobilePushOutcome.Retry),
            "InvalidCredentials" or "MismatchSenderId" => new(MobilePushOutcome.InvalidCredentials),
            _ => new(MobilePushOutcome.Rejected)
        };
    }
}

internal readonly record struct MobilePushResult(MobilePushOutcome Outcome, string? TicketId = null);

internal enum MobilePushOutcome
{
    Accepted,
    Pending,
    Unregistered,
    Retry,
    Rejected,
    InvalidCredentials
}
