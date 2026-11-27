using RTXLens.Plugins.Asus.Contracts.Configuration;
using RTXLens.Plugins.Asus.Contracts.Common;
using RTXLens.Presentation.Configuration;
using RTXLens.Tests.Infrastructure;
using RTXLens.UI.Tests.Fixtures;
using RTXLens.UI.Tests.TestDoubles;

namespace RTXLens.UI.Tests.Configuration;

internal static class EditorTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync(DoesNotSubmitUnchangedSettingsAsync);
        await runner.RunAsync(ConflictsPreserveUnsavedEditsAsync);
        await runner.RunAsync(AppliesTheWholeConfigurationWithItsVersionAsync);
        await runner.RunAsync(InvalidInputNeverReachesTheServiceAsync);
        await runner.RunAsync(WidgetProfileEditsSurviveApplyAndReloadAsync);
        runner.Run(DeviceRefreshPreservesAnExplicitMissingGpu);
        runner.Run(RefreshDoesNotDirtyTheConfiguration);
    }

    private static async Task DoesNotSubmitUnchangedSettingsAsync()
    {
        var api = new FakeMonitoringApi();
        var editor = new ConfigurationEditorViewModel(() => api);
        editor.Receive(api.Configuration);
        await editor.ApplyAsync(CancellationToken.None);
        TestAssert.Equal(1L, api.Configuration.Revision);
    }

    private static async Task ConflictsPreserveUnsavedEditsAsync()
    {
        var api = new FakeMonitoringApi();
        var editor = new ConfigurationEditorViewModel(() => api);
        editor.Receive(api.Configuration);
        editor.StreamIntervalMs = 250;
        api.Configuration = api.Configuration with { Revision = 2, Version = "test:2" };
        editor.Receive(api.Configuration);
        TestAssert.True(editor.HasExternalChanges, "An external update must be reported.");
        await editor.ApplyAsync(CancellationToken.None);
        TestAssert.True(editor is { IsDirty: true, HasExternalChanges: true }, "A conflict must preserve the draft.");
        TestAssert.Equal(250d, editor.StreamIntervalMs);
        await editor.ReloadAsync(CancellationToken.None);
        TestAssert.False(editor.IsDirty || editor.HasExternalChanges, "Reload must replace the draft.");
    }

    private static async Task AppliesTheWholeConfigurationWithItsVersionAsync()
    {
        var api = new FakeMonitoringApi();
        var editor = new ConfigurationEditorViewModel(() => api);
        editor.Receive(api.Configuration);
        editor.StreamIntervalMs = 300;
        editor.Plugins[0].Enabled = false;
        await editor.ApplyAsync(CancellationToken.None);
        TestAssert.Equal(300, api.Configuration.Configuration.Monitoring.StreamIntervalMs);
        TestAssert.False(api.Configuration.Configuration.Plugins["asus-astral"].Enabled, "Enablement must be applied live.");
        TestAssert.False(editor.IsDirty, "An acknowledged apply must clear the draft.");
    }

    private static async Task InvalidInputNeverReachesTheServiceAsync()
    {
        var api = new FakeMonitoringApi();
        var editor = new ConfigurationEditorViewModel(() => api);
        editor.Receive(api.Configuration);
        editor.StreamIntervalMs = double.NaN;
        await editor.ApplyAsync(CancellationToken.None);
        TestAssert.Equal(1L, api.Configuration.Revision);
        TestAssert.True(editor.IsDirty, "Invalid values should remain available for correction.");
    }

    private static async Task WidgetProfileEditsSurviveApplyAndReloadAsync()
    {
        var api = new FakeMonitoringApi();
        var editor = new ConfigurationEditorViewModel(() => api);
        editor.Receive(api.Configuration);
        editor.SetWidgetValue("Default", "showCrossbarClock", "true");
        editor.SetWidgetValue("Profile 2", "showSystemClock", "true");
        TestAssert.True(editor.IsDirty && editor.Widget.IsValid(), "Clock toggles must be valid pending edits.");
        TestAssert.False(editor.Widget.WithValue("Default", "backgroundMedia", "x").IsValid(), "Only desktop-managed settings may be saved.");
        TestAssert.False(editor.Widget.WithValue("Default", "currentMax", "100").IsValid(), "Reject invalid slider values.");
        await editor.ApplyAsync(CancellationToken.None);
        await editor.ReloadAsync(CancellationToken.None);
        var catalog = Contracts.Configuration.WidgetSettingDefinition.ById;
        var migrated = System.Text.Json.JsonSerializer.Deserialize(
            """{"profiles":{"Desk":{"connectedColor":"#123456","precision":"2"}}}""",
            Client.Serialization.ClientJsonContext.Default.WidgetConfiguration)!;
        TestAssert.True(migrated.IsValid() && !migrated.Profiles["Desk"].ContainsKey("connectedColor"),
            "Retired colors are discarded when loading a saved profile.");
        TestAssert.Equal("2", migrated.Value("Desk", catalog["precision"]));
        TestAssert.Equal("true", editor.Widget.Value("Profile 2", catalog["showCrossbarClock"]));
        TestAssert.Equal("true", editor.Widget.Value("Profile 2", catalog["showSystemClock"]));
        TestAssert.Equal("false", editor.Widget.Value("Default", catalog["showSystemClock"]));
        TestAssert.True(editor.AddWidgetProfile("Quiet desk"), "A new profile can inherit every setting.");
        TestAssert.False(editor.AddWidgetProfile("quiet DESK"), "Duplicate names are ambiguous in iCUE.");
        TestAssert.False(editor.AddWidgetProfile("Instance"), "The host settings name is reserved.");
        editor.DeleteWidgetProfile("Default");
        TestAssert.Equal("true", editor.Widget.Value("Default", catalog["showCrossbarClock"]));
        editor.StreamIntervalMs = 377;
        TestAssert.True(await editor.QueueWidgetPreviewAsync("Quiet desk", "thermal", CancellationToken.None), "Queue immediately.");
        var saved = api.Configuration.Configuration;
        TestAssert.Equal("thermal", saved.Widget.Preview!.Scenario);
        TestAssert.False(saved.Widget.Profiles.ContainsKey("Quiet desk"), "Preview cannot commit profile drafts.");
        TestAssert.True(saved.Monitoring.StreamIntervalMs != 377 && editor.IsDirty, "Preview preserves unrelated edits.");
        await editor.ApplyAsync(CancellationToken.None);
        await editor.ReloadAsync(CancellationToken.None);
        TestAssert.True(editor.Widget.Profiles.ContainsKey("Quiet desk"), "Empty named profiles survive reload.");
        TestAssert.Equal("true", editor.Widget.Value("Quiet desk", catalog["showCrossbarClock"]));
        editor.DeleteWidgetProfile("Quiet desk");
        await editor.ApplyAsync(CancellationToken.None);
        TestAssert.False(api.Configuration.Configuration.Widget.Profiles.ContainsKey("Quiet desk"), "Deleted profiles stay deleted.");
    }

    private static void DeviceRefreshPreservesAnExplicitMissingGpu()
    {
        var editor = new AsusSettingsEditorViewModel(new AsusOptions { DeviceId = "missing-gpu" });
        editor.SetDevices([new AsusDeviceInfo("gpu0", "RTX 5090")]);
        TestAssert.Equal("missing-gpu", editor.Build().DeviceId);
    }

    private static void RefreshDoesNotDirtyTheConfiguration()
    {
        var editor = new ConfigurationEditorViewModel(() => new FakeMonitoringApi());
        editor.Receive(UiTestData.Configuration);
        editor.SetDevices([new AsusDeviceInfo("gpu0", "RTX 5090")]);
        TestAssert.False(editor.IsDirty, "Discovery must not change the saved device selection.");
    }
}
