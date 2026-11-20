using System.Text.Json.Serialization;

namespace RTXLens.Contracts.Updates;

public sealed record ReleaseManifest
{
    public int SchemaVersion { get; init; }
    public string ReleaseTag { get; init; } = string.Empty;
    public string Desktop { get; init; } = string.Empty;
    public string Service { get; init; } = string.Empty;
    public string Widget { get; init; } = string.Empty;

    [JsonPropertyName("releaseUrl")]
    public string? PublishedReleaseUrl { get; init; }

    public const string RepositoryUrl = "https://github.com/Codeh4ck/RTXLens";
    public const string ManifestUrl = "https://rtxlens.com/updates/version.json";

    public const string TagPath = "/releases/tag/";
    public const string DownloadPath = "/releases/download/";
    public const string ArchiveName = "RTXLens-win-x64.zip";
    public const string LegacyRepositoryUrl = "https://github.com/Codeh4ck/RTX5090Monitor";

    public static bool IsReleaseUrl(string url, string tag) =>
        url == RepositoryUrl + TagPath + tag || url == LegacyRepositoryUrl + TagPath + tag;

    [JsonIgnore]
    public string ReleaseUrl => PublishedReleaseUrl ?? RepositoryUrl + TagPath + Uri.EscapeDataString(ReleaseTag);

    public bool IsValid() => SchemaVersion == 1
        && SemanticVersion.TryParse(Desktop, out _)
        && SemanticVersion.TryParse(Service, out _)
        && SemanticVersion.TryParse(Widget, out _)
        && ReleaseTag is { Length: > 1 } && ReleaseTag.StartsWith('v')
        && SemanticVersion.TryParse(ReleaseTag[1..], out _)
        && (PublishedReleaseUrl is null
            || IsReleaseUrl(PublishedReleaseUrl, ReleaseTag));
}
