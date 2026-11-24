using RTXLens.Core.Plugins;
using System.Globalization;
using RTXLens.Contracts.Alerts;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Contracts.Warnings;
using RTXLens.Warnings;

namespace RTXLens.Service.Services;

internal sealed class DeviceWarningEvaluator(HardwareCatalog hardware)
{
    private readonly Dictionary<string, TargetState> _targets = new(StringComparer.Ordinal);

    public DeviceWarningEvaluation Evaluate(SensorSnapshot snapshot, ServiceConfiguration configuration,
        DeviceWarningProfile[] profiles, SettingsProfile? automatic)
    {
        var contributions = new List<ConfiguredWarning>();
        var shutdowns = new List<WarningShutdownCandidate>();
        var pinLimits = new Dictionary<(string PluginId, string DeviceId), double>();
        var alerts = snapshot.Alerts.Where(alert => hardware.Find(alert.PluginId)?.PinProfiles is not { UpdateDetectionFlags: true } support
            || (alert.Kind != support.OvercurrentAlert && alert.Kind != support.ImbalanceAlert)).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var groups = snapshot.Sensors.Where(IsPinReading).ToLookup(reading => reading.Identity.PluginId);

        foreach (var profile in profiles.Where(profile => profile.Enabled))
        {
            var metadata = hardware.Find(profile.PluginId);
            if (metadata?.PinProfiles is not { } support || !configuration.Plugins.TryGetValue(profile.PluginId, out var plugin) || !plugin.Enabled)
            {
                continue;
            }

            var readings = groups[profile.PluginId].Where(reading =>
                support.Connectors.Length == 0 || reading.Identity.ConnectorId == profile.ConnectorId).ToArray();
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
            var limits = profile.Limits ?? ConnectorWarningLimits.From(support.UseAutomaticLimits ? automatic ?? preset : preset);
            var state = previous;
            if (state is null || state.Profile != profile || state.DeviceId != deviceId || state.Limits != limits || state.MaxAge != maxAge)
            {
                state = new TargetState(profile, deviceId, limits, maxAge);
                _targets[profile.Id] = state;
            }

            seen.Add(profile.Id);

            if (support.PublishPinLimits)
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

            Emit(PinWarningKind.Overcurrent, "Pin overcurrent", "current", current.Length == 6, overcurrent, "A");

            var high = current.MaxBy(reading => reading.Value);
            var low = current.MinBy(reading => reading.Value);
            var imbalance = high?.Value > 0 && low?.Value is { } lowValue
                ? 200 * (high.Value!.Value - lowValue) / (high.Value.Value + lowValue) : 0;
            var uneven = current.Length == 6 && high?.Value >= limits.ImbalanceActivationAmperes
                && imbalance >= limits.ImbalancePercentage;
            var imbalanceAt = current.Length == 6 ? current.Min(reading => reading.ObservedAt) : (DateTimeOffset?)null;
            var unbalanced = state.Sustained(0, uneven ? imbalanceAt : null, limits.ImbalanceMilliseconds);
            Emit(PinWarningKind.Imbalance, "Current imbalance", "imbalance", current.Length == 6, unbalanced ? [high!, low!] : [], "A",
                unbalanced ? imbalance : null, imbalanceAt);

            if (support.SupportsVoltage)
            {
                Emit(PinWarningKind.LowVoltage, "Low pin voltage", "voltage", voltage.Length == 6,
                    [.. voltage.Where(reading => reading.Value <= limits.LowPinVoltage)], "V");
                Emit(PinWarningKind.HighVoltage, "High pin voltage", "voltage", voltage.Length == 6,
                    [.. voltage.Where(reading => reading.Value >= limits.HighPinVoltage)], "V");
            }

            continue;

            void Emit(string kind, string title, string icon, bool complete, IReadOnlyList<SensorReading> offending,
                string unit, double? percentage = null, DateTimeOffset? evidenceAt = null)
            {
                var source = profile.ConnectorId is null ? metadata.DisplayName : $"{metadata.DisplayName} connector {profile.ConnectorId}";
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
                    ?? (support.LegacyWarningIds.TryGetValue(kind, out var legacyId) ? configuration.Warnings.Overrides.GetValueOrDefault(legacyId) : null)));

                if (profile.ShutdownAfterSeconds is not null)
                {
                    // Track each offending pin independently. A different pin must earn its own countdown.
                    foreach (var reading in kind == PinWarningKind.Imbalance ? offending.Take(1) : offending)
                    {
                        var key = kind == PinWarningKind.Imbalance ? observation.Id : $"{observation.Id}/P{reading.Identity.Channel}";
                        shutdowns.Add(new(key, profile, limits, definition, observation, evidenceAt ?? reading.ObservedAt, maxAge, source));
                    }
                }

                var alertKind = kind switch { PinWarningKind.Overcurrent => support.OvercurrentAlert, PinWarningKind.Imbalance => support.ImbalanceAlert, _ => null };
                if (observation.Active && alertKind is not null)
                {
                    alerts.Add(new(profile.PluginId, deviceId, alertKind, snapshot.Timestamp,
                        [.. offending.Select(reading => new AlertPin(reading.Identity.Channel!.Value, reading.Value!.Value))], percentage));
                }
            }
        }

        foreach (var id in _targets.Keys.Where(id => !seen.Contains(id)).ToArray())
        {
            _targets.Remove(id);
        }

        var deviceAlerts = alerts.ToLookup(alert => (alert.PluginId, alert.DeviceId));
        return new(snapshot with
        {
            Alerts = [.. alerts],
            Sensors = [.. snapshot.Sensors.Select(UpdateDetectionFlag)],
            PinWarningLimits = [.. pinLimits.Select(entry => new PinWarningLimit(entry.Key.PluginId, entry.Key.DeviceId, entry.Value))]
        }, contributions, shutdowns);

        SensorReading UpdateDetectionFlag(SensorReading reading)
        {
            if (hardware.Find(reading.Identity.PluginId)?.PinProfiles is not { UpdateDetectionFlags: true } support || reading.Quality != ReadingQuality.Valid)
            {
                return reading;
            }

            var active = deviceAlerts[(reading.Identity.PluginId, reading.Identity.DeviceId)];
            return reading.Identity.Metric switch
            {
                SensorMetric.OvercurrentDetected => reading with { Value = active.Any(alert => alert.Kind == support.OvercurrentAlert
                    && alert.Pins.Any(pin => pin.Channel == reading.Identity.Channel)) ? 1 : 0 },
                SensorMetric.ImbalanceDetected => reading with { Value = active.Any(alert => alert.Kind == support.ImbalanceAlert) ? 1 : 0 },
                _ => reading
            };
        }
    }

    private bool IsPinReading(SensorReading reading) =>
        reading.Identity is { Channel: >= 1 and <= 6, Metric: SensorMetric.Current or SensorMetric.Voltage }
        && hardware.Find(reading.Identity.PluginId)?.PinProfiles?.Component == reading.Identity.Component;

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
    WarningDefinition Definition, WarningObservation Observation, DateTimeOffset ObservedAt, TimeSpan MaxAge, string Source);
