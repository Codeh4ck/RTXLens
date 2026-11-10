using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using RTXLens.Client.Serialization;
using RTXLens.Contracts.Diagnostics;

namespace RTXLens.Client.Http;

public sealed class ServiceLogClient(HttpClient http)
{
    public async Task<ServiceLogPage> ReadHistoryAsync(long before, CancellationToken token)
    {
        var path = FormattableString.Invariant($"logs?before={before}&limit=250");
        using var response = await http.GetAsync(path, token).ConfigureAwait(false);
        return await ReadPageAsync(response, token).ConfigureAwait(false);
    }

    public async Task<ServiceLogPage> ClearAsync(CancellationToken token)
    {
        using var response = await http.DeleteAsync("logs", token).ConfigureAwait(false);
        return await ReadPageAsync(response, token).ConfigureAwait(false);
    }

    private static async Task<ServiceLogPage> ReadPageAsync(HttpResponseMessage response, CancellationToken token)
    {
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.ServiceLogPage, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The backend returned an empty event history.");
    }

    public async IAsyncEnumerable<ServiceLogPage> FollowAsync(string instance, long after,
        [EnumeratorCancellation] CancellationToken token)
    {
        var path = FormattableString.Invariant($"logs/stream?instance={Uri.EscapeDataString(instance)}&after={after}");
        using var response = await http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        while (await ReadLineAsync(reader, token).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            var page = JsonSerializer.Deserialize(line.AsSpan(6), ClientJsonContext.Default.ServiceLogPage)
                ?? throw new InvalidDataException("The backend returned an empty event stream update.");
            yield return page;
        }
    }

    private static async ValueTask<string?> ReadLineAsync(StreamReader reader, CancellationToken token)
    {
        using var heartbeatDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        heartbeatDeadline.CancelAfter(TimeSpan.FromSeconds(30));

        return await reader.ReadLineAsync(heartbeatDeadline.Token).ConfigureAwait(false);
    }
}
