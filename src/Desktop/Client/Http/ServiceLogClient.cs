using RTXLens.Contracts.Monitoring;
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
        var path = FormattableString.Invariant($"{ServiceRoutes.Logs}?before={before}&limit=250");

        using var response = await http.GetAsync(path, token);
        return await ReadPageAsync(response, token);
    }

    public async Task<ServiceLogPage> ClearAsync(CancellationToken token)
    {
        using var response = await http.DeleteAsync(ServiceRoutes.Logs, token);
        return await ReadPageAsync(response, token);
    }

    private static async Task<ServiceLogPage> ReadPageAsync(HttpResponseMessage response, CancellationToken token)
    {
        response.EnsureSuccessStatusCode();

        return Validate(await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.ServiceLogPage, token));
    }

    public async IAsyncEnumerable<ServiceLogPage> FollowAsync(string instance, long after, [EnumeratorCancellation] CancellationToken token)
    {
        var path = FormattableString.Invariant($"{ServiceRoutes.Logs}/stream?instance={Uri.EscapeDataString(instance)}&after={after}");

        using var response = await http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var reader = new StreamReader(stream);

        while (await ReadLineAsync(reader, token) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            yield return Validate(JsonSerializer.Deserialize(line.AsSpan(6), ClientJsonContext.Default.ServiceLogPage));
        }
    }

    private static ServiceLogPage Validate(ServiceLogPage? page)
    {
        if (page is null || string.IsNullOrWhiteSpace(page.InstanceId) || page.Entries is null || page.Entries.Any(entry => entry?.Source is null))
        {
            throw new InvalidDataException("The backend returned an incomplete event log page.");
        }

        return page;
    }

    private static async ValueTask<string?> ReadLineAsync(StreamReader reader, CancellationToken token)
    {
        using var heartbeatDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        heartbeatDeadline.CancelAfter(TimeSpan.FromSeconds(30));

        return await reader.ReadLineAsync(heartbeatDeadline.Token);
    }
}
