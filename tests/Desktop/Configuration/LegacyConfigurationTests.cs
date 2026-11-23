using System.Text.Json;
using RTXLens.Client.Http;
using RTXLens.Client.Serialization;
using RTXLens.Contracts.Warnings;
using RTXLens.Presentation.Configuration;
using RTXLens.Presentation.Settings;
using RTXLens.Presentation.Telemetry;
using RTXLens.Tests.Infrastructure;
using RTXLens.UI.Tests.Fixtures;
using RTXLens.UI.Tests.TestDoubles;

namespace RTXLens.UI.Tests.Configuration;

internal static class LegacyConfigurationTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync(LoadsLegacyConfigurationForStartupAndApplyAsync);
        await runner.RunAsync(PreservesCurrentServerWarningPolicyAsync);
    }

    private static async Task LoadsLegacyConfigurationForStartupAndApplyAsync()
    {
        var json = JsonSerializer.SerializeToNode(UiTestData.Configuration, ClientJsonContext.Default.ConfigurationSnapshot)!;
        json["configuration"]!.AsObject().Remove("warnings");
        using var http = new HttpClient(new JsonResponseHandler(json.ToJsonString())) { BaseAddress = new Uri("http://localhost:59273/") };
        var api = new MonitoringApiClient(http);
        var snapshot = await api.GetConfigurationAsync(CancellationToken.None);
        TestAssert.Equal(5, snapshot.Configuration.Warnings.Defaults.RepeatIntervalSeconds);
        var editor = new ConfigurationEditorViewModel(() => api);
        editor.Receive(snapshot);
        editor.Warnings.Defaults.RepeatWhileActive = true;
        TestAssert.True(editor.IsDirty, "Legacy configuration must load and remain editable without a null warning policy.");
        var dashboard = new DashboardViewModel();
        dashboard.SetConfiguration(snapshot);
        dashboard.Apply(UiTestData.Frame(), new AppPreferences(), DateTimeOffset.UtcNow);
        var applied = await api.SetConfigurationAsync(snapshot.Configuration, "legacy:1", CancellationToken.None);
        editor.Receive(applied);
        TestAssert.Equal(new WarningPolicy(), applied.Configuration.Warnings.Defaults);
    }

    private static async Task PreservesCurrentServerWarningPolicyAsync()
    {
        var snapshot = UiTestData.Configuration with
        {
            Configuration = UiTestData.Configuration.Configuration with
            {
                Warnings = new WarningOptions { Defaults = new WarningPolicy { RepeatWhileActive = true, RepeatIntervalSeconds = 15 } }
            }
        };
        var json = JsonSerializer.Serialize(snapshot, ClientJsonContext.Default.ConfigurationSnapshot);
        using var http = new HttpClient(new JsonResponseHandler(json)) { BaseAddress = new Uri("http://localhost:59273/") };
        var loaded = await new MonitoringApiClient(http).GetConfigurationAsync(CancellationToken.None);
        TestAssert.True(loaded.Configuration.Warnings.Defaults.RepeatWhileActive, "Current server policies must not be replaced with defaults.");
        TestAssert.Equal(15, loaded.Configuration.Warnings.Defaults.RepeatIntervalSeconds);
    }
}
