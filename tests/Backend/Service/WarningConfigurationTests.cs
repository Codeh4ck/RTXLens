using Microsoft.Extensions.Configuration;
using RTXLens.Plugins.GenericNvidia.Contracts.Warnings;
using RTXLens.Plugins.WireView.Contracts.Warnings;
using RTXLens.Plugins.Asus.Contracts.Warnings;
using System.Text.Json;
using System.Net;
using System.Text;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Monitoring;
using RTXLens.Contracts.Warnings;
using RTXLens.Service.Configuration;
using RTXLens.Tests.Infrastructure;
using RTXLens.Tests.TestDoubles;

namespace RTXLens.Tests.Service;

internal static class WarningConfigurationTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync(PersistsAndPublishesFiveSecondRemindersWithASlowStreamAsync);
        await runner.RunAsync(RejectsInvalidWarningOptionsWithoutChangingTheFileAsync);
        await runner.RunAsync(DiscoversInstalledDefinitionsWhilePluginsAreDisabledAsync);
        await runner.RunAsync(MigratesPersistedLegacyWarningSettingsOnStartupAsync);
        await runner.RunAsync(MigratesLegacyBootstrapThresholdsAsync);
        await runner.RunAsync(StartsFreshWithShippedBootstrapAndPersistsDefaultsAsync);
        runner.Run(ReadsEmptyBootstrapWarningObjects);
        runner.Run(KeepsInvalidWarningPoliciesInvalid);
    }

    private static async Task StartsFreshWithShippedBootstrapAndPersistsDefaultsAsync()
    {
        await using var host = await MonitoringTestHost.StartWithInstalledPluginsDisabledAsync(useShippedDefaults: true);
        var health = await host.GetHealthAsync();
        var loaded = await host.GetConfigurationAsync();
        var persisted = RuntimeJson.Deserialize(await File.ReadAllTextAsync(host.ConfigurationPath));

        TestAssert.Equal("running", health.GetProperty("status").GetString());
        TestAssert.True(health.GetProperty("configurationRevision").GetInt64() > 0,
            "Fresh installation must satisfy the installer's health check.");
        TestAssert.Equal(1000, loaded.Configuration.Monitoring.StreamIntervalMs);
        TestAssert.Equal(4, loaded.Configuration.Plugins.Count);
        TestAssert.True(loaded.Configuration.Plugins.Values.All(plugin => !plugin.Enabled),
            "The fresh-install test must not access hardware.");
        TestAssert.True(persisted.Warnings.IsValid(), "Fresh defaults must be persisted as valid warning options.");
        TestAssert.Equal(0, persisted.Warnings.Overrides.Count);
        TestAssert.Equal(5, persisted.Warnings.Defaults.RepeatIntervalSeconds);
        TestAssert.True(persisted.Warnings.Defaults.NotifyOnStart, "Default notifications must remain enabled.");
    }

    private static void ReadsEmptyBootstrapWarningObjects()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            """{"Warnings":{"Defaults":{},"Overrides":{},"CoreTemperatureC":82}}"""));
        using var bootstrap = (ConfigurationRoot)new ConfigurationBuilder().AddJsonStream(stream).Build();
        var warnings = RuntimeJson.ReadWarningOptions(bootstrap.GetSection("Warnings"));

        TestAssert.True(warnings.IsValid(), "Empty bootstrap objects must preserve valid initialized defaults.");
        TestAssert.Equal(0, warnings.Overrides.Count);
        TestAssert.Equal(5, warnings.Defaults.RepeatIntervalSeconds);
        TestAssert.Equal(82, warnings.AdditionalSettings!["CoreTemperatureC"].GetInt32());
    }

    private static void KeepsInvalidWarningPoliciesInvalid()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            """{"Warnings":{"Defaults":{"RepeatIntervalSeconds":1},"Overrides":{}}}"""));
        using var bootstrap = (ConfigurationRoot)new ConfigurationBuilder().AddJsonStream(stream).Build();
        var warnings = RuntimeJson.ReadWarningOptions(bootstrap.GetSection("Warnings"));

        TestAssert.False(warnings.IsValid(), "Invalid bootstrap intervals must still be rejected.");

        var persisted = RuntimeJson.Deserialize("""{"warnings":{"overrides":null}}""");
        TestAssert.False(persisted.Warnings.IsValid(), "Malformed saved configurations must not be silently reset.");
    }

    private static async Task PersistsAndPublishesFiveSecondRemindersWithASlowStreamAsync()
    {
        await using var host = await MonitoringTestHost.StartWithWireViewAsync(new WireViewTestState(), 100);
        var initial = await host.GetConfigurationAsync();
        TestAssert.Equal(5, initial.Configuration.Warnings.Defaults.RepeatIntervalSeconds);
        var candidate = initial.Configuration with
        {
            Monitoring = new MonitoringOptions { StreamIntervalMs = 30000 },
            Warnings = new WarningOptions { Defaults = new WarningPolicy { RepeatWhileActive = true } }
        };
        await host.SetConfigurationAsync(candidate);
        var persisted = RuntimeJson.Deserialize(await File.ReadAllTextAsync(host.ConfigurationPath));
        TestAssert.True(persisted.Warnings.Defaults.RepeatWhileActive, "The shared policy must persist.");
        TestAssert.Equal(5, persisted.Warnings.Defaults.RepeatIntervalSeconds);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(9));
        using var socket = await host.ConnectSensorClientAsync(timeout.Token);
        var first = await WebSocketMessageReader.ReadUntilAsync(socket, frame => frame.GetProperty("warnings").GetProperty("states").GetArrayLength() > 0, timeout.Token);
        var sequence = first.GetProperty("warnings").GetProperty("sequence").GetInt64();
        var next = await WebSocketMessageReader.ReadUntilAsync(socket, frame => frame.GetProperty("warnings").GetProperty("sequence").GetInt64() > sequence, timeout.Token);
        var state = next.GetProperty("warnings").GetProperty("states")[0];
        TestAssert.Equal(WireViewWarningIds.PinOvercurrent, state.GetProperty("typeId").GetString());
        TestAssert.Equal("Active", state.GetProperty("phase").GetString());
        TestAssert.Equal(5, state.GetProperty("policy").GetProperty("repeatIntervalSeconds").GetInt32());
        var firstAt = first.GetProperty("warnings").GetProperty("states")[0].GetProperty("lastNotificationAt").GetDateTimeOffset();
        var nextAt = state.GetProperty("lastNotificationAt").GetDateTimeOffset();
        TestAssert.True(nextAt - firstAt >= TimeSpan.FromSeconds(5) && nextAt - firstAt < TimeSpan.FromSeconds(7), "Warning delivery must not wait for the 30-second sensor cadence.");
    }
    private static async Task DiscoversInstalledDefinitionsWhilePluginsAreDisabledAsync()
    {
        await using var host = await MonitoringTestHost.StartWithInstalledPluginsDisabledAsync();
        using var document = JsonDocument.Parse(await host.Client.GetStringAsync("/plugins"));
        var plugins = document.RootElement.GetProperty("plugins").EnumerateArray().ToArray();
        TestAssert.True(plugins.All(plugin => !plugin.GetProperty("enabled").GetBoolean()), "Catalogue discovery must work without enabling hardware.");
        TestAssert.Equal(2, plugins.Single(plugin => plugin.GetProperty("id").GetString() == "generic-nvidia").GetProperty("warnings").GetArrayLength());
        TestAssert.Equal(4, plugins.Single(plugin => plugin.GetProperty("id").GetString() == "asus-astral").GetProperty("warnings").GetArrayLength());
        var wireViewWarnings = plugins.Single(plugin => plugin.GetProperty("id").GetString() == "wireview-pro2").GetProperty("warnings");
        TestAssert.Equal(7, wireViewWarnings.GetArrayLength());
        TestAssert.True(wireViewWarnings.EnumerateArray().Any(warning =>
            warning.GetProperty("id").GetString() == WireViewWarningIds.ProfilePinOvercurrent),
            "The software pin warning must be available in the notification catalog.");
        TestAssert.Equal(0, plugins.Single(plugin => plugin.GetProperty("id").GetString() == "msi-psu").GetProperty("warnings").GetArrayLength());
    }
    private static async Task MigratesPersistedLegacyWarningSettingsOnStartupAsync()
    {
        var legacy = Fixtures.WarningTestData.Configuration() with
        {
            Warnings = new WarningOptions
            {
                Overrides = new Dictionary<string, WarningPolicy> { ["AsusCoreTemperature"] = new() { RepeatWhileActive = true } },
                AdditionalSettings = new Dictionary<string, JsonElement> { ["coreTemperatureC"] = JsonSerializer.SerializeToElement(82) }
            }
        };
        await using var host = await MonitoringTestHost.StartWithAsusAsync(new AsusTestState(), new WireViewTestState(), legacy);
        var loaded = await host.GetConfigurationAsync();
        TestAssert.True(loaded.Configuration.Warnings.Overrides.ContainsKey(GenericNvidiaWarningIds.CoreTemperature), "Startup must expose migrated stable IDs.");
        var persisted = await File.ReadAllTextAsync(host.ConfigurationPath);
        TestAssert.False(persisted.Contains("AsusCoreTemperature", StringComparison.Ordinal), "Startup must persist migrated aliases.");
    }
    private static async Task MigratesLegacyBootstrapThresholdsAsync()
    {
        await using var host = await MonitoringTestHost.StartWithAsusAsync(new AsusTestState(), bootstrapOverrides: new Dictionary<string, string?>
        {
            ["Warnings:CoreTemperatureC"] = "81", ["Warnings:MemoryTemperatureC"] = "85"
        });
        var loaded = await host.GetConfigurationAsync();
        var options = loaded.Configuration.Plugins["generic-nvidia"].Options!.Value.Deserialize<RTXLens.Plugins.GenericNvidia.Contracts.Configuration.GenericNvidiaOptions>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        TestAssert.Equal<double?>(81, options.CoreTemperatureWarningC);
        TestAssert.Equal<double?>(85, options.MemoryTemperatureWarningC);
    }
    private static async Task RejectsInvalidWarningOptionsWithoutChangingTheFileAsync()
    {
        await using var host = await MonitoringTestHost.StartWithoutPluginsAsync();
        var initial = await host.GetConfigurationAsync();
        var before = await File.ReadAllTextAsync(host.ConfigurationPath);
        var invalid = initial.Configuration with { Warnings = new WarningOptions { Defaults = new WarningPolicy { RepeatIntervalSeconds = 1 } } };
        using var body = new StringContent(RuntimeJson.Serialize(invalid), Encoding.UTF8, "application/json");
        using var response = await host.Client.PutAsync("/configuration", body);
        TestAssert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        TestAssert.Equal(before, await File.ReadAllTextAsync(host.ConfigurationPath));
    }
}
