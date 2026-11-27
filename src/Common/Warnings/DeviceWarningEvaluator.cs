using RTXLens.Contracts.Discovery;
using System.Globalization;
using RTXLens.Contracts.Alerts;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Contracts.Warnings;

namespace RTXLens.Warnings;

public sealed class DeviceWarningEvaluator(Func<string, PluginHardwareMetadata?> findHardware)
{
    private readonly Dictionary<string, TargetState> _targets = new(StringComparer.Ordinal);

    public DeviceWarningEvaluation Evaluate(SensorSnapshot snapshot, ServiceConfiguration configuration, DeviceWarningProfile[] profiles,
        SettingsProfile? automatic)
    {
        var preset = automatic?.GpuModel == "5080" ? SettingsProfile.Rtx5080 : SettingsProfile.Rtx5090;
        return Evaluate(snapshot, configuration, profiles, ConnectorWarningLimits.From(preset), ConnectorWarningLimits.From(automatic ?? preset));
    }

    public DeviceWarningEvaluation Evaluate(SensorSnapshot snapshot, ServiceConfiguration configuration, DeviceWarningProfile[] profiles,
        ConnectorWarningLimits defaults, ConnectorWarningLimits automaticLimits)
    {
        var contributions = new List<ConfiguredWarning>();
        var shutdowns = new List<WarningShutdownCandidate>();
        var pinLimits = new Dictionary<(string PluginId, string DeviceId), double>();

        var alerts = snapshot.Alerts.Where(alert => findHardware(alert.PluginId)?.PinProfiles is not { UpdateDetectionFlags: true } support
                                                    || (alert.Kind != support.OvercurrentAlert && alert.Kind != support.ImbalanceAlert)).ToList();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var groups = snapshot.Sensors.Where(IsPinReading).ToLookup(reading => reading.Identity.PluginId);

        foreach (var profile in profiles.Where(profile => profile.Enabled))
        {
            var metadata = findHardware(profile.PluginId);
            if (metadata?.PinProfiles is not { } support || !configuration.Plugins.TryGetValue(profile.PluginId, out var plugin) || !plugin.Enabled)
            {
                continue;
            }

            var readings = groups[profile.PluginId]
                .Where(reading => support.Connectors.Length == 0 || reading.Identity.ConnectorId == profile.ConnectorId)
                .ToArray();

            var previous = _targets.GetValueOrDefault(profile.Id);
            var available = snapshot.Plugins.Any(health => health.PluginId == profile.PluginId && health.State == PluginState.Ready);
            var maxAge = TimeSpan.FromMilliseconds(plugin.StaleAfterMs);

            var fresh = available
                ? readings.Where(reading => reading is { Quality: ReadingQuality.Valid, Value: { } value }
                                            && double.IsFinite(value) && value >= 0 && snapshot.Timestamp - reading.ObservedAt <= maxAge
                                            && reading.ObservedAt <= snapshot.Timestamp).ToArray()
                : [];

            var deviceIds = fresh.Select(reading => reading.Identity.DeviceId).Distinct(StringComparer.Ordinal).Take(2).ToArray();
            var deviceId = profile.DeviceId ?? (deviceIds.Length == 1 ? deviceIds[0] : previous?.DeviceId);

            if (deviceId is null)
            {
                continue;
            }

            var limits = profile.Limits ?? (support.UseAutomaticLimits ? automaticLimits : defaults);
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

            var pins = profile.DeviceId is null && deviceIds.Length != 1
                ? []
                : fresh.Where(reading => reading.Identity.DeviceId == deviceId).ToArray();

            var current = pins
                .Where(reading => reading.Identity.Metric == SensorMetric.Current)
                .DistinctBy(reading => reading.Identity.Channel)
                .ToArray();

            var voltage = pins
                .Where(reading => reading.Identity.Metric == SensorMetric.Voltage && reading.Value > 0)
                .DistinctBy(reading => reading.Identity.Channel).ToArray();

            var overcurrent = new List<SensorReading>();
            for (var pin = 1; pin <= 6; pin++)
            {
                var reading = current.FirstOrDefault(item => item.Identity.Channel == pin);
                if (state.Sustained(pin, reading?.Value is { } amperes && amperes + 0.000001 >= limits.OvercurrentAmperes ? reading.ObservedAt : null,
                        limits.OvercurrentMilliseconds))
                {
                    overcurrent.Add(reading!);
                }
            }

            var high = current.MaxBy(reading => reading.Value);
            var low = current.MinBy(reading => reading.Value);

            Emit(PinWarningKind.Overcurrent, "Pin overcurrent", "current", current.Length == 6, overcurrent, "A",
                Threshold("Pin overcurrent", SensorUnit.Amperes, limits.OvercurrentAmperes, high, current.Length == 6,
                        high?.Value + 0.000001 >= limits.OvercurrentAmperes) with
                    {
                        DurationMs = limits.OvercurrentMilliseconds
                    });

            var imbalance = high?.Value > 0 && low?.Value is { } lowValue ? 200 * (high.Value!.Value - lowValue) / (high.Value.Value + lowValue) : 0;
            var uneven = current.Length == 6 && high?.Value >= limits.ImbalanceActivationAmperes && imbalance >= limits.ImbalancePercentage;
            var imbalanceAt = current.Length == 6 ? current.Min(reading => reading.ObservedAt) : (DateTimeOffset?)null;
            var unbalanced = state.Sustained(0, uneven ? imbalanceAt : null, limits.ImbalanceMilliseconds);

            Emit(PinWarningKind.Imbalance, "Current imbalance", "imbalance", current.Length == 6, unbalanced ? [high!, low!] : [], "A",
                new WarningThreshold("Imbalance", SensorUnit.Percent, limits.ImbalancePercentage, current.Length == 6 ? imbalance : null,
                    current.Length == 6 ? uneven : null)
                {
                    MinimumCurrent = limits.ImbalanceActivationAmperes, DurationMs = limits.ImbalanceMilliseconds
                }, unbalanced ? imbalance : null, imbalanceAt);

            if (support.SupportsVoltage)
            {
                Voltage(PinWarningKind.LowVoltage, "Low pin voltage", limits.LowPinVoltage, isMinimum: true);
                Voltage(PinWarningKind.HighVoltage, "High pin voltage", limits.HighPinVoltage, isMinimum: false);
            }

            continue;

            void Voltage(string kind, string title, double limit, bool isMinimum)
            {
                var offending = voltage.Where(reading => isMinimum ? reading.Value <= limit : reading.Value >= limit).ToArray();
                var complete = voltage.Length == 6;
                var breached = offending.Length > 0;

                Emit(kind, title, "voltage", complete, offending, "V",
                    new WarningThreshold(title, SensorUnit.Volts, limit, null, breached || complete ? breached : null) { IsMinimum = isMinimum });
            }

            void Emit(string kind, string title, string icon, bool complete, IReadOnlyList<SensorReading> offending,
                string unit, WarningThreshold threshold, double? percentage = null, DateTimeOffset? evidenceAt = null)
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
                var observation =
                    new WarningObservation(profile.PluginId, deviceId, typeId, complete || offending.Count > 0, offending.Count > 0, detail)
                    {
                        ProfileId = profile.Id,
                        ConnectorId = profile.ConnectorId,
                        Threshold = threshold with
                        {
                            OffendingPins =
                            [.. offending.Select(reading => new WarningThresholdPin(reading.Identity.Channel!.Value, reading.Value!.Value))]
                        }
                    };

                contributions.Add(new(definition, observation, profile.NotificationPolicy
                                                               ?? (support.LegacyWarningIds.TryGetValue(kind, out var legacyId)
                                                                   ? configuration.Warnings.Overrides.GetValueOrDefault(legacyId)
                                                                   : null)));

                if (profile.ShutdownAfterSeconds is not null)
                {
                    foreach (var reading in kind == PinWarningKind.Imbalance ? offending.Take(1) : offending)
                    {
                        var key = kind == PinWarningKind.Imbalance ? observation.Id : $"{observation.Id}/P{reading.Identity.Channel}";
                        shutdowns.Add(new(key, profile, limits, definition, observation, evidenceAt ?? reading.ObservedAt, maxAge, source));
                    }
                }

                var alertKind = kind switch
                {
                    PinWarningKind.Overcurrent => support.OvercurrentAlert, PinWarningKind.Imbalance => support.ImbalanceAlert, _ => null
                };

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

        return new(
            snapshot with
            {
                Alerts = [.. alerts],
                Sensors = [.. snapshot.Sensors.Select(UpdateDetectionFlag)],
                PinWarningLimits = [.. pinLimits.Select(entry => new PinWarningLimit(entry.Key.PluginId, entry.Key.DeviceId, entry.Value))]
            }, contributions, shutdowns);

        SensorReading UpdateDetectionFlag(SensorReading reading)
        {
            if (findHardware(reading.Identity.PluginId)?.PinProfiles is not { UpdateDetectionFlags: true } support || reading.Quality != ReadingQuality.Valid)
            {
                return reading;
            }

            var active = deviceAlerts[(reading.Identity.PluginId, reading.Identity.DeviceId)];
            return reading.Identity.Metric switch
            {
                SensorMetric.OvercurrentDetected => reading with
                {
                    Value = active.Any(alert => alert.Kind == support.OvercurrentAlert && alert.Pins.Any(pin => pin.Channel == reading.Identity.Channel)) ? 1 : 0
                },
                SensorMetric.ImbalanceDetected => reading with { Value = active.Any(alert => alert.Kind == support.ImbalanceAlert) ? 1 : 0 },
                _ => reading
            };
        }
    }

    private bool IsPinReading(SensorReading reading) =>
        reading.Identity is { Channel: >= 1 and <= 6, Metric: SensorMetric.Current or SensorMetric.Voltage }
        && findHardware(reading.Identity.PluginId)?.PinProfiles?.Component == reading.Identity.Component;

    private static WarningThreshold Threshold(string title, SensorUnit unit, double limit, SensorReading? reading, bool complete, bool outside) =>
        new(title, unit, limit, reading?.Value, outside || complete ? outside : null) { Pin = reading?.Identity.Channel };

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

public sealed record DeviceWarningEvaluation(SensorSnapshot Snapshot, IReadOnlyList<ConfiguredWarning> Warnings, IReadOnlyList<WarningShutdownCandidate> Shutdowns);

public sealed record WarningShutdownCandidate(
    string Key,
    DeviceWarningProfile Profile,
    ConnectorWarningLimits Limits,
    WarningDefinition Definition,
    WarningObservation Observation,
    DateTimeOffset ObservedAt,
    TimeSpan MaxAge,
    string Source);