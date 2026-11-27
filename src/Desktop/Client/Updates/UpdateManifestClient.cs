using System.Text.Json;
using RTXLens.Client.Serialization;
using RTXLens.Contracts.Updates;

namespace RTXLens.Client.Updates;

public sealed class UpdateManifestClient(HttpClient http)
{
    public async Task<ReleaseManifest> GetAsync(CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseManifest.ManifestUrl);
        request.Headers.UserAgent.ParseAdd("RTXLens-UpdateCheck/1.0");
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };

        using var response = await http.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        
        var text = await response.Content.ReadAsStringAsync(token);

        if (text.Length > 16384)
        {
            throw new InvalidDataException("Update manifest is too large.");
        }

        var manifest = JsonSerializer.Deserialize(text, ClientJsonContext.Default.ReleaseManifest);

        if (manifest is null || !manifest.IsValid())
        {
            throw new InvalidDataException("Unsupported update manifest.");
        }

        return manifest;
    }
}
