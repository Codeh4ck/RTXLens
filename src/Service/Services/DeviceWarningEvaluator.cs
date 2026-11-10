using System.Globalization;
using RTXLens.Contracts.Alerts;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Contracts.Warnings;
using RTXLens.Plugins.Asus.Contracts.Warnings;
using RTXLens.Warnings;

namespace RTXLens.Service.Services;

internal sealed class DeviceWarningEvaluator
{
    private readonly Dictionary<string, TargetState> _targets = new(StringComparer.Ordinal);

    public DeviceWarningEvaluation Evaluate(SensorSnapshot snapshot, ServiceConfiguration configuration,
        DeviceWarningProfile[] profiles, SettingsProfile? automatic)
    {
        var contributions = new List<ConfiguredWarning>();
        var shutdowns = new List<WarningShutdownCandidate>();
        var pinLimits = new Dictionary<(string PluginId, string DeviceId), double>();
        var alerts = snapshot.Alerts.Where(alert => alert.PluginId != "asus-astral"
            || alert.Kind is not (AsusAlertKinds.WireOvercurrent or AsusAlertKinds.CurrentImbalance)).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var groups = snapshot.Sensors.Where(IsPinReading).ToLookup(reading => reading.Identity.PluginId);

        foreach (var profile in profiles.Where(profile => profile.Enabled))
        {
            if (!configuration.Plugins.TryGetValue(profile.PluginId, out var plugin) || !plugin.Enabled)
            {
                continue;
            }

            var readings = groups[profile.PluginId].Where(reading =>
                profile.PluginId != "msi-psu" || reading.Identity.ConnectorId == profile.ConnectorId).ToArray();
            var previous = _targets.GetValueOrDefault(profile.Id);
            var available = snapshot.Plugins.Any(health => health.PluginId == profile.PluginId && health.State == PluginState.Ready);
            var maxAge = TimeSpan.FromMilliseconds(plugin.StaleAfterMs);
            var fresh = available ? readings.Where(reading => reading is { Quality: ReadingQuality.Valid, Value: { } value }
                && double.IsFinite(value) && value >= 0 && snapshot.Timestamp - reading.ObservedAt <= maxAge
                && reading.ObservedAt <= snapshot.Timestamp).ToArray() : [];
            var deviceIds = fresh.Select(reading => reading.Identity.DeviceId).Distinct(StringComparer.Ordinal).Take(2).ToArray();
            var deviceId = profile.DeviceId ?? (deviceIds.Length == 1 ? deviceIds[0] : previous?.DeviceId);

            if (deviceId is null)
            {
                continue;
            }

            var preset = automatic?.GpuModel == "5080" ? SettingsProfile.Rtx5080 : SettingsProfile.Rtx5090;
            var limits = profile.Limits ?? ConnectorWarningLimits.From(profile.PluginId == "asus-astral" ? automatic ?? preset : preset);
            var state = previous;
            if (state is null || state.Profile != profile || state.DeviceId != deviceId || state.Limits != limits || state.MaxAge != maxAge)
            {
                state = new TargetState(profile, deviceId, limits, maxAge);
                _targets[profile.Id] = state;
            }

            seen.Add(profile.Id);

            if (profile.PluginId != "msi-psu")
            {
                var key = (profile.PluginId, deviceId);
                pinLimits[key] = Math.Min(pinLimits.GetValueOrDefault(key, double.PositiveInfinity), limits.OvercurrentAmperes);
            }
            var pins = profile.DeviceId is null && deviceIds.Length != 1 ? []
                : fresh.Where(reading => reading.Identity.DeviceId == deviceId).ToArray();
            var current = pins.Where(reading => reading.Identity.Metric == SensorMetric.Current)
                .DistinctBy(reading => reading.Identity.Channel).ToArray();
            var voltage = pins.Where(reading => reading.Identity.Metric == SensorMetric.Voltage && reading.Value > 0)
                .DistinctBy(reading => reading.Identity.Channel).ToArray();

            var overcurrent = new List<SensorReading>();
            for (var pin = 1; pin <= 6; pin++)
            {
                var reading = current.FirstOrDefault(item => item.Identity.Channel == pin);
                if (state.Sustained(pin, reading?.Value is { } amperes && amperes + 0.000001 >= limits.OvercurrentAmperes ? reading.ObservedAt : null, limits.OvercurrentMilliseconds))
                {
                    overcurrent.Add(reading!);
                }
            }

            Emit("overcurrent", "Pin overcurrent", "current", current.Length == 6, overcurrent, "A");

            var high = current.MaxBy(reading => reading.Value);
            var low = current.MinBy(reading => reading.Value);
            var imbalance = high?.Value > 0 && low?.Value is { } lowValue
                ? 200 * (high.Value!.Value - lowValue) / (high.Value.Value + lowValue) : 0;
            var uneven = current.Length == 6 && high?.Value >= limits.ImbalanceActivationAmperes
                && imbalance >= limits.ImbalancePercentage;
            var imbalanceAt = current.Length == 6 ? current.Min(reading => reading.ObservedAt) : (DateTimeOffset?)null;
            var unbalanced = state.Sustained(0, uneven ? imbalanceAt : null, limits.ImbalanceMilliseconds);
            Emit("imbalance", "Current imbalance", "imbalance", current.Length == 6, unbalanced ? [high!, low!] : [], "A",
                unbalanced ? imbalance : null, imbalanceAt);

            if (profile.PluginId != "msi-psu")
            {
                Emit("low-voltage", "Low pin voltage", "voltage", voltage.Length == 6,
                    [.. voltage.Where(reading => reading.Value <= limits.LowPinVoltage)], "V");
                Emit("high-voltage", "High pin voltage", "voltage", voltage.Length == 6,
                    [.. voltage.Where(reading => reading.Value >= limits.HighPinVoltage)], "V");
            }

            void Emit(string kind, string title, string icon, bool complete, IReadOnlyList<SensorReading> offending,
                string unit, double? percentage = null, DateTimeOffset? evidenceAt = null)
            {
                var source = SourceLabel(profile);
                var typeId = profile.PluginId + "/monitor-" + kind;
                var detail = string.Join(" / ", offending.Select(reading => $"P{reading.Identity.Channel}: {Number(reading.Value!.Value)} {unit}"));
                if (percentage is { } difference)
                {
                    detail += $" - {Number(difference)}% difference";
                }

                if (detail.Length > 0)
                {
                    detail += $" (Profile: {profile.Name})";
                }

                var definition = new WarningDefinition(typeId, profile.PluginId, $"{source} · {title}", icon);
                var observation = new WarningObservation(profile.PluginId, deviceId, typeId, complete || offending.Count > 0,
                    offending.Count > 0, detail) { ProfileId = profile.Id };
                contributions.Add(new(definition, observation, profile.NotificationPolicy
                    ?? LegacyPolicy(configuration.Warnings, profile.PluginId, kind)));

                if (profile.ShutdownAfterSeconds is not null)
                {
                    // Track each offending pin independently. A different pin must earn its own countdown.
                    foreach (var reading in kind == "imbalance" ? offending.Take(1) : offending)
                    {
                        var key = kind == "imbalance" ? observation.Id : $"{observation.Id}/P{reading.Identity.Channel}";
                        shutdowns.Add(new(key, profile, limits, definition, observation, evidenceAt ?? reading.ObservedAt, maxAge));
                    }
                }

                if (observation.Active && ((profile.PluginId == "asus-astral" && kind is "overcurrent" or "imbalance")
                    || (profile.PluginId == "wireview-pro2" && kind == "overcurrent")))
                {
                    var alertKind = profile.PluginId == "wireview-pro2" ? ActiveSettingsProfile.WireViewProfileOvercurrent
                        : kind == "overcurrent" ? AsusAlertKinds.WireOvercurrent : AsusAlertKinds.CurrentImbalance;
                    alerts.Add(new(profile.PluginId, deviceId, alertKind, snapshot.Timestamp,
                        [.. offending.Select(reading => new AlertPin(reading.Identity.Channel!.Value, reading.Value!.Value))], percentage));
                }
            }
        }

        foreach (var id in _targets.Keys.Where(id => !seen.Contains(id)).ToArray())
        {
            _targets.Remove(id);
        }

        var astralAlerts = alerts.Where(alert => alert.PluginId == "asus-astral").ToLookup(alert => alert.DeviceId);
        return new(snapshot with
        {
            Alerts = [.. alerts],
            Sensors = [.. snapshot.Sensors.Select(UpdateAstralFlag)],
            PinWarningLimits = [.. pinLimits.Select(entry => new PinWarningLimit(entry.Key.PluginId, entry.Key.DeviceId, entry.Value))]
        }, contributions, shutdowns);

        SensorReading UpdateAstralFlag(SensorReading reading)
        {
            if (reading.Identity.PluginId != "asus-astral" || reading.Quality != ReadingQuality.Valid)
            {
                return reading;
            }

            var active = astralAlerts[reading.Identity.DeviceId];
            return reading.Identity.Metric switch
            {
                SensorMetric.OvercurrentDetected => reading with { Value = active.Any(alert => alert.Kind == AsusAlertKinds.WireOvercurrent
                    && alert.Pins.Any(pin => pin.Channel == reading.Identity.Channel)) ? 1 : 0 },
                SensorMetric.ImbalanceDetected => reading with { Value = active.Any(alert => alert.Kind == AsusAlertKinds.CurrentImbalance) ? 1 : 0 },
                _ => reading
            };
        }
    }

    public static string SourceLabel(DeviceWarningProfile profile) => profile.PluginId switch
    {
        "asus-astral" => "ROG Astral",
        "wireview-pro2" => "WireView Pro 2",
        _ => $"MSI PSU connector {profile.ConnectorId}"
    };

    private static bool IsPinReading(SensorReading reading) =>
        reading.Identity is { Channel: >= 1 and <= 6, Metric: SensorMetric.Current or SensorMetric.Voltage } and ({ PluginId: "msi-psu", Component: SensorComponent.PsuPin }
            or { PluginId: "asus-astral" or "wireview-pro2", Component: SensorComponent.GpuPin });

    private static WarningPolicy? LegacyPolicy(WarningOptions options, string pluginId, string kind)
    {
        var legacyId = (pluginId, kind) switch
        {
            ("asus-astral", "overcurrent") => AsusWarningIds.Overcurrent,
            ("asus-astral", "imbalance") => AsusWarningIds.Imbalance,
            ("asus-astral", "low-voltage") => AsusWarningIds.LowVoltage,
            ("asus-astral", "high-voltage") => AsusWarningIds.HighVoltage,
            _ => ""
        };

        return options.Overrides.GetValueOrDefault(legacyId);
    }

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private sealed record TargetState(DeviceWarningProfile Profile, string DeviceId, ConnectorWarningLimits Limits, TimeSpan MaxAge)
    {
        private readonly Dictionary<int, (DateTimeOffset Start, DateTimeOffset Last)> _pending = [];

        public bool Sustained(int key, DateTimeOffset? observedAt, int milliseconds)
        {
            if (observedAt is not { } observed)
            {
                _pending.Remove(key);
                return false;
            }

            if (!_pending.TryGetValue(key, out var period) || observed < period.Last || observed - period.Last > MaxAge)
            {
                period = (observed, observed);
            }

            _pending[key] = (period.Start, observed);
            return observed - period.Start >= TimeSpan.FromMilliseconds(milliseconds);
        }
    }
}

internal sealed record DeviceWarningEvaluation(SensorSnapshot Snapshot, IReadOnlyList<ConfiguredWarning> Warnings,
    IReadOnlyList<WarningShutdownCandidate> Shutdowns);

internal sealed record WarningShutdownCandidate(string Key, DeviceWarningProfile Profile, ConnectorWarningLimits Limits,
    WarningDefinition Definition, WarningObservation Observation, DateTimeOffset ObservedAt, TimeSpan MaxAge);
