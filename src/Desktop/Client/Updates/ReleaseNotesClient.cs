using System.Net.Http.Json;
using RTXLens.Client.Serialization;
using RTXLens.Contracts.Updates;

namespace RTXLens.Client.Updates;

public sealed class ReleaseNotesClient(HttpClient http)
{
    public async Task<ReleaseNotes> GetAsync(CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseNotes.FeedUrl);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };

        using var response = await http.SendAsync(request, token);
        response.EnsureSuccessStatusCode();

        var notes = await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.ReleaseNotes, token);
        return notes is not null && notes.IsValid() ? notes : throw new InvalidDataException("Unsupported release notes.");
    }
}
