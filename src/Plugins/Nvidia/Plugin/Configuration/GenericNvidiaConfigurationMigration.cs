using RTXLens.Plugins.Asus.Contracts;
using System.Text.Json;
using RTXLens.Contracts.Configuration;
using RTXLens.Plugins.GenericNvidia.Contracts.Configuration;

namespace RTXLens.Plugins.GenericNvidia.Configuration;

internal static class GenericNvidiaConfigurationMigration
{
    public static ServiceConfiguration Upgrade(ServiceConfiguration configuration)
    {
        if (configuration.Plugins.ContainsKey(GenericNvidiaPlugin.PluginId)
            || !configuration.Plugins.TryGetValue(AsusIdentity.Id, out var asus))
        {
            return configuration;
        }

        var json = asus.Options;
        var legacy = configuration.Warnings?.AdditionalSettings is { } settings
            ? new Dictionary<string, JsonElement>(settings, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        var legacyCore = legacy.Remove("coreTemperatureC", out var coreValue) ? coreValue.GetDouble() : (double?)null;
        var legacyMemory = legacy.Remove("memoryTemperatureC", out var memoryValue) ? memoryValue.GetDouble() : (double?)null;

        var options = new GenericNvidiaOptions
        {
            DeviceId = Read("deviceId") is { ValueKind: JsonValueKind.String } device ? device.GetString() : null,
            CoreTemperatureWarningC = Read("coreTemperatureWarningC") is { ValueKind: JsonValueKind.Number } core ? core.GetDouble() : legacyCore ?? 84,
            MemoryTemperatureWarningC = Read("memoryTemperatureWarningC") is { ValueKind: JsonValueKind.Number } memory ? memory.GetDouble() : legacyMemory ?? 95
        };

        var plugins = new Dictionary<string, PluginSettings>(configuration.Plugins, StringComparer.Ordinal)
        {
            [GenericNvidiaPlugin.PluginId] = GenericNvidiaSettings.WithOptions(new PluginSettings
            {
                PollIntervalMs = asus.PollIntervalMs,
                StaleAfterMs = asus.StaleAfterMs
            }, options)
        };

        return configuration with
        {
            Plugins = plugins,
            Warnings = (configuration.Warnings ?? new()) with { AdditionalSettings = legacy.Count == 0 ? null : legacy }
        };

        JsonElement? Read(string name)
        {
            if (json is not { ValueKind: JsonValueKind.Object } value)
            {
                return null;
            }

            foreach (var property in value.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value;
                }
            }

            return null;
        }
    }
}
