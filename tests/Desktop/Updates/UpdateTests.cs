using System.Net;
using System.Text.Json.Nodes;
using RTXLens.Client.Http;
using RTXLens.Client.Updates;
using RTXLens.Contracts.Updates;
using RTXLens.Presentation.Settings;
using RTXLens.Presentation.Updates;
using RTXLens.Tests.Infrastructure;
using RTXLens.UI.Tests.TestDoubles;

namespace RTXLens.UI.Tests.Updates;

internal static class UpdateTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        runner.Run(ComparesSemanticVersionsNumerically);
        runner.Run(UsesCalendarMonthsAndRealWeeklyIntervals);
        await runner.RunAsync(ReadsPublishedReleaseNotesAsync);
        await runner.RunAsync(ChecksOnStartupAndPersistsTheScheduledTimestampAsync);
        await runner.RunAsync(DisabledChecksStillAllowManualChecksAsync);
        await runner.RunAsync(DismissalsArePerComponentAndVersionAsync);
        await runner.RunAsync(RemindersExpireWithoutWaitingForTheWeeklyPollAsync);
        await runner.RunAsync(OfflineAndUnknownServiceVersionsDoNotBreakChecksAsync);
        await runner.RunAsync(OlderServicesCanOmitTheVersionEndpointAsync);
        await runner.RunAsync(MigratesUpdateDefaultsAndPersistsOptOutAsync);
    }

    private static void ComparesSemanticVersionsNumerically()
    {
        TestAssert.True(SemanticVersion.TryParse("1.10.0", out var latest), "Stable versions must parse.");
        TestAssert.True(SemanticVersion.TryParse("1.9.9", out var installed), "Stable versions must parse.");
        TestAssert.True(latest.CompareTo(installed) > 0, "Versions must not use lexical ordering.");

        foreach (var invalid in new[] { "01.0.0", "1.0", "1.0.0-preview", "-1.0.0", "1.0.0.0", "1.0.0+test" })
        {
            TestAssert.False(SemanticVersion.TryParse(invalid, out _), "Malformed and preview versions must not be advertised.");
        }

        TestAssert.False(new ReleaseManifest { ReleaseTag = null! }.IsValid(), "Malformed manifests must be rejected safely.");
    }

    private static void UsesCalendarMonthsAndRealWeeklyIntervals()
    {
        var start = new DateTimeOffset(2026, 1, 31, 12, 0, 0, TimeSpan.Zero);
        TestAssert.Equal(start.AddDays(1), UpdateViewModel.NextCheck(start, UpdateFrequency.Daily));
        TestAssert.Equal(start.AddDays(7), UpdateViewModel.NextCheck(start, UpdateFrequency.Weekly));
        TestAssert.Equal(new DateTimeOffset(2026, 2, 28, 12, 0, 0, TimeSpan.Zero), UpdateViewModel.NextCheck(start, UpdateFrequency.Monthly));
    }

    private static async Task ReadsPublishedReleaseNotesAsync()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "releaseTag": "v1.0.6",
              "publishedAt": "2026-11-09T05:36:39Z",
              "releaseUrl": "https://github.com/Codeh4ck/RTX5090Monitor/releases/tag/v1.0.6",
              "changelog": "- Add **Safeguard+ History**.\n- Improve updates."
            }
            """;

        using var http = new HttpClient(new JsonResponseHandler(json));
        var notes = await new ReleaseNotesClient(http).GetAsync(CancellationToken.None);

        TestAssert.Equal("v1.0.6", notes.ReleaseTag);
        TestAssert.Equal("- Add **Safeguard+ History**.\n- Improve updates.", notes.Changelog);
        TestAssert.Equal("https://github.com/Codeh4ck/RTX5090Monitor/releases/download/v1.0.6/RTXLens-win-x64.zip",
            ReleaseNotes.DownloadFor(notes.ReleaseUrl).AbsoluteUri);
        TestAssert.False((notes with { ReleaseTag = "v1.0.7" }).IsValid(),
            "The release URL and changelog tag must match.");
    }

    private static async Task ChecksOnStartupAndPersistsTheScheduledTimestampAsync()
    {
        using var fixture = new UpdateTestFixture();
        var first = fixture.Create();
        first.Configure(true, UpdateFrequency.Weekly);
        await first.CheckAsync(false, CancellationToken.None);
        await first.CheckAsync(false, CancellationToken.None);
        TestAssert.Equal(1, fixture.Requests);
        fixture.Clock.Now = fixture.Clock.Now.AddDays(6);
        await first.CheckAsync(false, CancellationToken.None);
        TestAssert.Equal(1, fixture.Requests);
        fixture.Clock.Now = fixture.Clock.Now.AddDays(1);
        await first.CheckAsync(false, CancellationToken.None);
        TestAssert.Equal(2, fixture.Requests);

        var restarted = fixture.Create();
        TestAssert.False(restarted.LastChecked == "Never", "The last successful check must survive restart.");
        restarted.Configure(true, UpdateFrequency.Weekly);
        await restarted.CheckAsync(false, CancellationToken.None);
        TestAssert.Equal(3, fixture.Requests);
    }

    private static async Task DisabledChecksStillAllowManualChecksAsync()
    {
        using var fixture = new UpdateTestFixture();
        var model = fixture.Create();
        model.Configure(false, UpdateFrequency.Daily);
        await model.CheckAsync(false, CancellationToken.None);
        TestAssert.Equal(0, fixture.Requests);
        await model.CheckAsync(true, CancellationToken.None);
        TestAssert.Equal(1, fixture.Requests);
        TestAssert.Equal(2, model.Available.Count);
    }

    private static async Task DismissalsArePerComponentAndVersionAsync()
    {
        using var fixture = new UpdateTestFixture();
        var model = fixture.Create();
        await model.CheckAsync(false, CancellationToken.None);
        await model.DismissAsync(model.Available.Single(notice => notice.Component == "desktop"), true, CancellationToken.None);
        TestAssert.Equal("service", model.Available.Single().Component);

        var restarted = fixture.Create();
        await restarted.CheckAsync(false, CancellationToken.None);
        TestAssert.Equal("service", restarted.Available.Single().Component);
        fixture.Manifest = fixture.Manifest with { Desktop = "1.2.0", ReleaseTag = "v1.2.0" };
        await restarted.CheckAsync(true, CancellationToken.None);
        TestAssert.Equal(2, restarted.Available.Count);
        TestAssert.True(restarted.Available.All(notice => notice.ReleaseUrl.EndsWith("/v1.2.0", StringComparison.Ordinal)),
            "Every notice must link to the release that supplied its manifest.");
    }

    private static async Task RemindersExpireWithoutWaitingForTheWeeklyPollAsync()
    {
        using var fixture = new UpdateTestFixture();
        var model = fixture.Create();
        model.Configure(true, UpdateFrequency.Weekly);
        await model.CheckAsync(false, CancellationToken.None);

        foreach (var notice in model.Available.ToArray())
        {
            await model.DismissAsync(notice, false, CancellationToken.None);
        }

        TestAssert.False(model.HasUpdates, "Remind later hides the current notices.");
        fixture.Clock.Now = fixture.Clock.Now.AddHours(24);
        await model.CheckAsync(false, CancellationToken.None);
        TestAssert.Equal(1, fixture.Requests);
        TestAssert.Equal(2, model.Available.Count);
    }

    private static async Task OfflineAndUnknownServiceVersionsDoNotBreakChecksAsync()
    {
        using var fixture = new UpdateTestFixture { Fail = true };
        var model = fixture.Create();
        await model.CheckAsync(false, CancellationToken.None);
        await model.CheckAsync(false, CancellationToken.None);
        TestAssert.Equal(1, fixture.Requests);
        TestAssert.False(model.Checking, "Offline checks must complete without throwing.");
        fixture.Fail = false;
        fixture.ServiceVersion = null;
        await model.CheckAsync(true, CancellationToken.None);
        TestAssert.Equal("desktop", model.Available.Single().Component);
        TestAssert.Equal("Version unavailable", model.ServiceVersion);
    }

    private static async Task OlderServicesCanOmitTheVersionEndpointAsync()
    {
        using var http = new HttpClient(new VersionResponseHandler(HttpStatusCode.NotFound)) { BaseAddress = new Uri("http://localhost:59273/") };
        TestAssert.True(await new MonitoringApiClient(http).GetVersionAsync(CancellationToken.None) is null, "An older service must remain usable.");
    }

    private static async Task MigratesUpdateDefaultsAndPersistsOptOutAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "rtx-update-preferences-" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            var store = new PreferencesStore(path);
            await store.SaveAsync(new AppPreferences());
            var legacy = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            legacy.Remove("automaticallyCheckForUpdates");
            legacy.Remove("updateFrequency");
            await File.WriteAllTextAsync(path, legacy.ToJsonString());
            var preferences = store.Load();
            TestAssert.True(preferences.AutomaticallyCheckForUpdates, "Existing users should receive the enabled default.");
            TestAssert.Equal(UpdateFrequency.Daily, preferences.UpdateFrequency);
            await store.SaveAsync(preferences with { AutomaticallyCheckForUpdates = false, UpdateFrequency = UpdateFrequency.Monthly });
            TestAssert.False(store.Load().AutomaticallyCheckForUpdates, "An explicit opt-out must be retained.");
            TestAssert.Equal(UpdateFrequency.Monthly, store.Load().UpdateFrequency);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
