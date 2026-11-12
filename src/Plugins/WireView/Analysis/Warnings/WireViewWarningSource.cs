using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Contracts.Warnings;
using RTXLens.Plugins.WireView.Contracts.Warnings;
using RTXLens.Warnings;

namespace RTXLens.Plugins.WireView.Analysis.Warnings;

public sealed class WireViewWarningSource : IWarningSource
{
    public string PluginId => "wireview-pro2";
    public IReadOnlyList<WarningDefinition> Definitions => [
        new(WireViewWarningIds.Overtemperature, PluginId, "Overtemperature", "temperature") { LegacyIds = ["WireViewOvertemperature"] },
        new(WireViewWarningIds.Overcurrent, PluginId, "Total overcurrent", "current") { LegacyIds = ["WireViewOvercurrent"] },
        new(ActiveSettingsProfile.WireViewProfileOvercurrent, PluginId, "GPU profile pin overcurrent", "current"),
        new(WireViewWarningIds.PinOvercurrent, PluginId, "Pin overcurrent", "current") { LegacyIds = ["WireViewPinOvercurrent"] },
        new(WireViewWarningIds.Overpower, PluginId, "Overpower", "power") { LegacyIds = ["WireViewOverpower"] },
        new(WireViewWarningIds.Imbalance, PluginId, "Current imbalance", "imbalance") { LegacyIds = ["WireViewImbalance"] },
        new(WireViewWarningIds.UnknownFault, PluginId, "Unrecognized device fault", "warning") { LegacyIds = ["WireViewUnknownFault"] },
    ];
    public WarningObservation[] Observe(SensorSnapshot snapshot, PluginSettings settings)
    {
        if (!settings.Enabled || !snapshot.Plugins.Any(health => health.PluginId == PluginId && health.State == PluginState.Ready))
        {
            return [];
        }

        var observations = new List<WarningObservation>();

        var statuses = snapshot.Sensors.Where(reading => reading.Identity.PluginId == PluginId && reading.Identity.Metric == SensorMetric.FaultStatus
            && reading is { Quality: ReadingQuality.Valid, Value: { } value } && double.IsFinite(value)
            && snapshot.Timestamp - reading.ObservedAt <= TimeSpan.FromMilliseconds(settings.StaleAfterMs));

        foreach (var reading in statuses)
        {
            var flags = (ushort)reading.Value!.Value;

            Fault(WireViewWarningIds.Overtemperature, 0x02);
            Fault(WireViewWarningIds.Overcurrent, 0x04);
            Fault(WireViewWarningIds.PinOvercurrent, 0x08);
            Fault(WireViewWarningIds.Overpower, 0x10);
            Fault(WireViewWarningIds.Imbalance, 0x20);
            Fault(WireViewWarningIds.UnknownFault, unchecked((ushort)~0x3E));

            continue;

            void Fault(string typeId, ushort mask) => observations.Add(new WarningObservation(PluginId, reading.Identity.DeviceId, typeId,
                true, (flags & mask) != 0, "Reported by WireView Pro II firmware."));
        }
        
        var currentPins = snapshot.Sensors.Where(reading => reading.Identity.PluginId == PluginId
            && reading.Identity is { Component: SensorComponent.GpuPin, Metric: SensorMetric.Current }).ToArray();

        foreach (var deviceId in currentPins.Select(reading => reading.Identity.DeviceId)
            .Concat(statuses.Select(reading => reading.Identity.DeviceId)).Distinct(StringComparer.Ordinal))
        {
            var active = snapshot.SettingsProfile is { Profile.UseWireViewOvercurrentFallback: true } applied && applied.WireViewDeviceId == deviceId;
            var alert = active ? snapshot.Alerts.FirstOrDefault(item => item.PluginId == PluginId && item.DeviceId == deviceId
                && item.Kind == ActiveSettingsProfile.WireViewProfileOvercurrent) : null;
            var complete = !active || currentPins.Count(reading => reading.Identity.DeviceId == deviceId
                && reading is { Quality: ReadingQuality.Valid, Value: >= 0 }
                && snapshot.Timestamp - reading.ObservedAt <= TimeSpan.FromMilliseconds(settings.StaleAfterMs)) == 6;
            var detail = alert is null ? "" : string.Join(" / ", alert.Pins.Select(pin => $"P{pin.Channel}: {pin.CurrentA:0.##} A"));

            observations.Add(new WarningObservation(PluginId, deviceId, ActiveSettingsProfile.WireViewProfileOvercurrent,
                alert is not null || complete, alert is not null, detail));
        }

        return [.. observations];
    }
}
