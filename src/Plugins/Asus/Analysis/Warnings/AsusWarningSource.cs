using RTXLens.Plugins.Asus.Contracts;
using System.Globalization;
using System.Text.Json;
using RTXLens.Contracts.Alerts;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Contracts.Warnings;
using RTXLens.Plugins.Asus.Contracts.Configuration;
using RTXLens.Plugins.Asus.Contracts.Warnings;
using RTXLens.Warnings;

namespace RTXLens.Plugins.Asus.Analysis.Warnings;

public sealed class AsusWarningSource : IWarningSource
{
    public string PluginId => AsusIdentity.Id;
    public IReadOnlyList<WarningDefinition> Definitions => [
        new(AsusWarningIds.Overcurrent, PluginId, "Pin overcurrent", "current") { LegacyIds = ["AsusOvercurrent"] },
        new(AsusWarningIds.Imbalance, PluginId, "Current imbalance", "imbalance") { LegacyIds = ["AsusImbalance"] },
        new(AsusWarningIds.LowVoltage, PluginId, "Low pin voltage", "voltage") { LegacyIds = ["AsusLowVoltage"] },
        new(AsusWarningIds.HighVoltage, PluginId, "High pin voltage", "voltage") { LegacyIds = ["AsusHighVoltage"] },
    ];

    public WarningObservation[] Observe(SensorSnapshot snapshot, PluginSettings settings)
    {
        if (!settings.Enabled || !snapshot.Plugins.Any(health => health.PluginId == PluginId && health.State == PluginState.Ready))
        {
            return [];
        }

        var options = settings.Options?.Deserialize(AsusWarningJsonContext.Default.AsusOptions) ?? new AsusOptions();
        var readings = snapshot.Sensors.Where(reading => reading.Identity.PluginId == PluginId
            && reading is { Quality: ReadingQuality.Valid, Value: { } value } && double.IsFinite(value)
            && snapshot.Timestamp - reading.ObservedAt <= TimeSpan.FromMilliseconds(settings.StaleAfterMs)).ToArray();

        var observations = new List<WarningObservation>();

        foreach (var device in readings.GroupBy(reading => reading.Identity.DeviceId))
        {
            var sensors = device.ToArray();
            var pins = sensors.Where(sensor => sensor.Identity.Component == SensorComponent.GpuPin).ToArray();
            var currentsComplete = pins.Count(sensor => sensor.Identity.Metric == SensorMetric.Current) == 6;

            foreach (var (typeId, alertKind) in new[] { (AsusWarningIds.Overcurrent, AsusAlertKinds.WireOvercurrent), (AsusWarningIds.Imbalance, AsusAlertKinds.CurrentImbalance) })
            {
                var alert = snapshot.Alerts.FirstOrDefault(alert => alert.PluginId == PluginId && alert.DeviceId == device.Key && alert.Kind == alertKind);
                var detail = alert is null ? string.Empty : string.Join(" / ", alert.Pins.Select(pin => $"P{pin.Channel}: {Number(pin.CurrentA)} A"));

                if (alert?.PercentageDifference is { } percentage)
                {
                    detail += $" - {Number(percentage)}% difference";
                }

                observations.Add(new WarningObservation(PluginId, device.Key, typeId, alert is not null || currentsComplete, alert is not null, detail));
            }

            var voltages = pins.Where(sensor => sensor.Identity.Metric == SensorMetric.Voltage).ToArray();

            var active = snapshot.SettingsProfile;
            var profile = active?.AppliesToConnector(AsusIdentity.Hardware.Association, device.Key) == true ? active.Profile : null;
            var lowVoltage = profile?.LowPinVoltage ?? 11.5;
            var highVoltage = profile?.HighPinVoltage ?? 12.5;
            Voltage(AsusWarningIds.LowVoltage, [.. voltages.Where(sensor => sensor.Value is > 0 && sensor.Value <= lowVoltage)]);
            Voltage(AsusWarningIds.HighVoltage, [.. voltages.Where(sensor => sensor.Value >= highVoltage)]);

            continue;

            void Voltage(string typeId, SensorReading[] offending) => observations.Add(new WarningObservation(PluginId, device.Key, typeId,
                offending.Length > 0 || voltages.Length == 6, offending.Length > 0,
                string.Join(" / ", offending.Select(pin => $"P{pin.Identity.Channel}: {Number(pin.Value!.Value)} V"))));


        }

        return [.. observations];
    }

    public PluginSettings MigrateSettings(PluginSettings settings, IDictionary<string, JsonElement> legacySettings)
    {
        var options = settings.Options?.Deserialize(AsusWarningJsonContext.Default.AsusOptions) ?? new AsusOptions();
        var changed = false;

        if (legacySettings.Remove("coreTemperatureC", out var core))
        {
            if (options.CoreTemperatureWarningC is null) { options = options with { CoreTemperatureWarningC = core.GetDouble() }; changed = true; }
        }

        if (legacySettings.Remove("memoryTemperatureC", out var memory))
        {
            if (options.MemoryTemperatureWarningC is null)
            {
                options = options with { MemoryTemperatureWarningC = memory.GetDouble() };
                changed = true;
            }
        }

        return changed ? settings with { Options = JsonSerializer.SerializeToElement(options, AsusWarningJsonContext.Default.AsusOptions) } : settings;
    }
    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
