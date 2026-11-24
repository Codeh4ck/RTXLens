using RTXLens.Plugins.Asus.Contracts.Warnings;
using RTXLens.Plugins.GenericNvidia.Contracts;
using System.Globalization;
using System.Text.Json;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Contracts.Warnings;
using RTXLens.Plugins.GenericNvidia.Contracts.Configuration;
using RTXLens.Plugins.GenericNvidia.Contracts.Warnings;
using RTXLens.Warnings;

namespace RTXLens.Plugins.GenericNvidia.Analysis.Warnings;

public sealed class GenericNvidiaWarningSource : IWarningSource
{
    public string PluginId => NvidiaIdentity.Id;

    public IReadOnlyList<WarningDefinition> Definitions =>
    [
        new(GenericNvidiaWarningIds.CoreTemperature, PluginId, "Core temperature", "temperature")
        {
            LegacyIds = ["AsusCoreTemperature", AsusWarningIds.CoreTemperature]
        },
        new(GenericNvidiaWarningIds.MemoryTemperature, PluginId, "VRAM temperature", "temperature")
        {
            LegacyIds = ["AsusMemoryTemperature", AsusWarningIds.MemoryTemperature]
        }
    ];

    public WarningObservation[] Observe(SensorSnapshot snapshot, PluginSettings settings)
    {
        if (!settings.Enabled || !snapshot.Plugins.Any(health => health.PluginId == PluginId && health.State == PluginState.Ready))
        {
            return [];
        }

        var options = settings.Options?.Deserialize(GenericNvidiaWarningJsonContext.Default.GenericNvidiaOptions) ?? new GenericNvidiaOptions();
        var readings = snapshot.Sensors.Where(reading => reading.Identity.PluginId == PluginId
            && reading is { Quality: ReadingQuality.Valid, Value: { } value } && double.IsFinite(value)
            && snapshot.Timestamp - reading.ObservedAt <= TimeSpan.FromMilliseconds(settings.StaleAfterMs)).ToArray();
        var observations = new List<WarningObservation>();

        foreach (var device in readings.GroupBy(reading => reading.Identity.DeviceId))
        {
            Add(GenericNvidiaWarningIds.CoreTemperature, SensorComponent.GpuCore, options.CoreTemperatureWarningC);
            Add(GenericNvidiaWarningIds.MemoryTemperature, SensorComponent.GpuMemory, options.MemoryTemperatureWarningC);
            continue;

            void Add(string typeId, SensorComponent component, double? threshold)
            {
                var temperature = device.FirstOrDefault(reading => reading.Identity.Component == component
                    && reading.Identity.Metric == SensorMetric.Temperature)?.Value;
                var detail = temperature is { } value && threshold is { } limit
                    ? $"{Number(value)} °C - threshold {Number(limit)} °C" : string.Empty;

                observations.Add(new WarningObservation(PluginId, device.Key, typeId,
                    threshold is null || temperature is not null, threshold is not null && temperature >= threshold, detail));
            }
        }

        return [.. observations];
    }

    public PluginSettings MigrateSettings(PluginSettings settings, IDictionary<string, JsonElement> legacySettings)
    {
        var options = settings.Options?.Deserialize(GenericNvidiaWarningJsonContext.Default.GenericNvidiaOptions) ?? new GenericNvidiaOptions();

        if (legacySettings.Remove("coreTemperatureC", out var core) && options.CoreTemperatureWarningC == 81)
        {
            options = options with { CoreTemperatureWarningC = core.GetDouble() };
        }

        if (legacySettings.Remove("memoryTemperatureC", out var memory) && options.MemoryTemperatureWarningC == 85)
        {
            options = options with { MemoryTemperatureWarningC = memory.GetDouble() };
        }

        return settings with { Options = JsonSerializer.SerializeToElement(options, GenericNvidiaWarningJsonContext.Default.GenericNvidiaOptions) };
    }

    private static string Number(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}
