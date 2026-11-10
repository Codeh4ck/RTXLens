using System.Text.Json.Nodes;
using RTXLens.Presentation.Settings;
using RTXLens.Tests.Infrastructure;

namespace RTXLens.UI.Tests.Settings;

internal static class PreferenceTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        runner.Run(RejectsInvalidPreferences);
        await runner.RunAsync(PersistsExitConfirmationChoiceAsync);
        await runner.RunAsync(PersistsAndRecoversFromCorruptPreferencesAsync);
        await runner.RunAsync(MigratesWindowHeightOnceWithoutLosingPreferencesAsync);
        await runner.RunAsync(WindowPlacementPreferencesAreIndependentAsync);
    }

    private static async Task WindowPlacementPreferencesAreIndependentAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "rtxlens-window-layout-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new PreferencesStore(path);
            var settings = new AppSettingsViewModel(store.Load());
            TestAssert.True(settings is { RememberWindowSize: true, RememberWindowPosition: true },
                "Both remember settings must be enabled by default.");

            settings.SaveWindowPlacement("main", new WindowPlacement(1100, 800, -1300, 50));
            settings.SaveWindowPlacement("release-notes", new WindowPlacement(700, 440, 100, 200));
            settings.RememberWindowPosition = false;
            await store.SaveAsync(settings.Value);

            var reloaded = new AppSettingsViewModel(store.Load());
            TestAssert.True(reloaded is { RememberWindowSize: true, RememberWindowPosition: false }, "Position is independent of the size mode.");
            TestAssert.Equal(new WindowPlacement(1100, 800, -1300, 50), reloaded.Value.WindowPlacements!["main"]);
            TestAssert.Equal(new WindowPlacement(700, 440, 100, 200), reloaded.Value.WindowPlacements!["release-notes"]);
            reloaded.RememberWindowPosition = true;
            reloaded.RememberWindowSize = false;
            TestAssert.True(reloaded.RememberWindowPosition, "Turning size persistence off must preserve position persistence.");
            await store.SaveAsync(reloaded.Value);
            TestAssert.False(store.Load().RememberWindowSize, "An explicit opt-out must survive reloading.");

            var previous = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            previous["autoResizeWindows"] = true;
            await File.WriteAllTextAsync(path, previous.ToJsonString());
            TestAssert.True(store.Load().RememberWindowSize, "The old auto-resize mode must no longer prevent remembering size.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task MigratesWindowHeightOnceWithoutLosingPreferencesAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "rtx-window-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            TestAssert.Equal(1565d, new AppPreferences().WindowWidth);
            TestAssert.Equal(1103d, new AppPreferences().WindowHeight);
            var store = new PreferencesStore(path);
            await store.SaveAsync(new AppPreferences { WindowWidth = 1120, WindowHeight = 800, Theme = AppTheme.Dark, StartMinimized = true });
            var legacy = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            legacy.Remove("windowLayoutVersion");
            await File.WriteAllTextAsync(path, legacy.ToJsonString());
            var migrated = store.Load();
            TestAssert.True(store.LastError is null, store.LastError ?? "Legacy preferences must load successfully.");
            TestAssert.Equal(1050d, migrated.WindowHeight);
            TestAssert.Equal(1120d, migrated.WindowWidth);
            TestAssert.True(migrated is { Theme: AppTheme.Dark, StartMinimized: true }, "Migrating window height must preserve other preferences.");
            await store.SaveAsync(migrated with { WindowHeight = 900 });
            TestAssert.Equal(900d, store.Load().WindowHeight);
            legacy["windowHeight"] = 1200;
            await File.WriteAllTextAsync(path, legacy.ToJsonString());
            TestAssert.Equal(1200d, store.Load().WindowHeight);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task PersistsExitConfirmationChoiceAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "rtx-exit-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new PreferencesStore(path);
            var settings = new AppSettingsViewModel(store.Load());
            TestAssert.True(settings.ConfirmBeforeExit, "New installations must confirm exits.");

            settings.ConfirmBeforeExit = false;
            await store.SaveAsync(settings.Value);
            TestAssert.True(store.Load().SuppressExitConfirmation, "Opting out must survive restarting the app.");

            var reloaded = new AppSettingsViewModel(store.Load());
            reloaded.ConfirmBeforeExit = true;
            await store.SaveAsync(reloaded.Value);
            TestAssert.True(!store.Load().SuppressExitConfirmation, "Settings must allow confirmation to be restored.");

            var legacy = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            legacy.Remove("suppressExitConfirmation");
            await File.WriteAllTextAsync(path, legacy.ToJsonString());
            TestAssert.True(new AppSettingsViewModel(store.Load()).ConfirmBeforeExit, "Existing installations must confirm exits after upgrading.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void RejectsInvalidPreferences()
    {
        TestAssert.Throws<ArgumentException>(() => (new AppPreferences { AccentColor = null! }).Validate());
        TestAssert.Throws<ArgumentException>(() => (new AppPreferences { ServiceAddress = "file:///C:/Windows/" }).Validate());
        TestAssert.Throws<ArgumentException>(() => (new AppPreferences { ServiceAddress = "http://user:secret@localhost/" }).Validate());
        TestAssert.Throws<ArgumentException>(() => (new AppPreferences { TextScale = double.NaN }).Validate());
        TestAssert.Throws<ArgumentException>(() => (new AppPreferences { WireViewPsuConnector = 0 }).Validate());
        TestAssert.Throws<ArgumentException>(() => (new AppPreferences { AsusPsuConnector = 3 }).Validate());
    }

    private static async Task PersistsAndRecoversFromCorruptPreferencesAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "rtx-ui-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new PreferencesStore(path);
            var value = new AppPreferences { Theme = AppTheme.Light, StartMinimized = true, ShowWireViewPsuCurrent = true,
                WireViewPsuConnector = 2, ShowAsusPsuCurrent = true, AsusPsuConnector = 1 };
            await store.SaveAsync(value);
            TestAssert.Equal(value, store.Load());
            await File.WriteAllTextAsync(path, "{broken");
            TestAssert.Equal(new AppPreferences(), store.Load());
            TestAssert.True(store.LastError is not null, "Corrupt preferences must be reported.");
        }
        finally { File.Delete(path); }
    }
}
