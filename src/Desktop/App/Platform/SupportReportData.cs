using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using RTXLens.Contracts.Diagnostics;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Plugins.Asus.Contracts;
using RTXLens.Plugins.GenericNvidia.Contracts;
using RTXLens.Plugins.Msi.Contracts;
using RTXLens.Plugins.WireView.Contracts;

namespace RTXLens.App.Platform;

// Only reviewed values cross this boundary. A future API field must not enter a support bundle by default.
internal static partial class SupportReportData
{

    [GeneratedRegex(@"\b(?:RTX|GTX)\s*(?<series>[0-9]{3,4})(?:\s*(?<ti>Ti)\b)?(?:\s*(?<super>SUPER)\b)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GpuModel();

    private static readonly HashSet<string> EventCodes = new(StringComparer.Ordinal)
    {
        "desktop.configuration-changed",
        "configuration.applied", "configuration.apply-rejected", "configuration.loaded",
        "configuration.notification-failed", "configuration.reload-rejected", "configuration.reloaded",
        "configuration.rollback-failed", "configuration.rolled-back", "configuration.save-failed",
        "connector.profile-selected", "connector.resolution-failed", "connector.resolution-recovered",
        "connector.source-fallback", "connector.source-selected", "connector.source-unavailable",
        "gpu.profile-detection-failed", "gpu.profile-detection-recovered", "gpu.selected",
        "logs.overloaded", "logs.storage-unavailable", "logs.windows-unavailable",
        "mobile.admin-command-failed", "mobile.admin-pipe-unavailable", "mobile.history-record-invalid",
        "mobile.history-write-failed", "mobile.listening", "mobile.no-ip",
        "mobile.paired", "mobile.pairing-requested", "mobile.push-accepted",
        "mobile.push-cleanup-failed", "mobile.push-delivery-failed", "mobile.push-queue-full",
        "mobile.push-receipt-queue-full", "mobile.push-receipt-queue-recovered", "mobile.revoked",
        "mobile.setup-failed", "mobile.stopped", "msi.fan-automatic-restored",
        "msi.fan-automatic", "msi.fan-fallback", "msi.fan-restore-failed",
        "msi.fan-settings-changed", "plugin.auto-detection", "plugin.configuration-failed",
        "plugin.device-lost", "plugin.device-ready", "plugin.no-device",
        "plugin.poll-failed", "plugin.poll-persistent-failure", "plugin.readings-fresh",
        "plugin.readings-stale", "plugin.recovered", "plugin.restart-failed",
        "plugin.restarted", "protection.profile-deleted", "protection.profile-saved",
        "protection.profiles-invalid", "protection.profiles-read-failed", "protection.shutdown-cancelled",
        "protection.shutdown-countdown", "protection.shutdown-failed", "protection.shutdown-rejected",
        "protection.shutdown-requested", "protection.shutdown-unconfirmed", "protection.warning-active",
        "protection.warning-ended", "protection.warning-unavailable", "safeguard.action-failed",
        "safeguard.email-accepted", "safeguard.email-expired", "safeguard.email-retry",
        "safeguard.email-test-accepted", "safeguard.shutdown-requested", "safeguard.simulated",
        "safeguard.telemetry-missing", "safeguard.triggered", "service.ready",
        "service.restart-requested", "service.stopping", "wireview.config-applied",
        "wireview.config-read-failed", "wireview.config-read", "wireview.config-write-unverified",
        "wireview.fault-clear-failed", "wireview.fault-clear-sent",
    };

    public static string EventCode(string code) => EventCodes.Contains(code) ? code : "other";

    public static object Event(ServiceLogEntry entry) => new
    {
        timestamp = entry.Timestamp.UtcDateTime, entry.Level, code = EventCode(entry.Code),
        plugin = entry.Source switch
        {
            AsusIdentity.PluginName or AsusIdentity.DisplayName or AsusIdentity.Id => AsusIdentity.Id,
            NvidiaIdentity.PluginName or NvidiaIdentity.DisplayName or NvidiaIdentity.ShortName or NvidiaIdentity.Id => NvidiaIdentity.Id,
            MsiIdentity.PluginName or MsiIdentity.DisplayName or MsiIdentity.Id => MsiIdentity.Id,
            WireViewIdentity.PluginName or WireViewIdentity.Id => WireViewIdentity.Id,
            _ => "other"
        }
    };

    private static string Plugin(string? id) => id is "generic-nvidia" or "asus-astral" or "msi-psu" or "wireview-pro2" ? id : "other";

    public static object Reading(SensorReading reading) => new
    {
        plugin = Plugin(reading.Identity.PluginId), reading.Identity.Component, reading.Identity.Channel,
        reading.Identity.Metric, reading.Value, reading.Unit, reading.Quality, observedAt = reading.ObservedAt.UtcDateTime
    };

    public static object[] Sources(SensorSnapshot? snapshot) =>
    [
        .. (snapshot?.Selections ?? [])
        .Where(s => s.Name is "gpu-status/power" or "gpu-status/current" or "gpu-status/voltage")
        .Select(object (s) => new { s.Name, s.State, plugin = Plugin(s.SensorKey?.Split('/', 2)[0]) })
    ];

    public static object Snapshot(SensorSnapshot snapshot) => new
    {
        timestamp = snapshot.Timestamp.UtcDateTime, snapshot.Sequence,
        readings = snapshot.Sensors.Select(Reading), sources = Sources(snapshot),
        warnings = snapshot.Warnings?.States.Select(w => new { plugin = Plugin(w.PluginId), w.Phase, startedAt = w.StartedAt.UtcDateTime }),
        alerts = snapshot.Alerts.Select(a => new { plugin = Plugin(a.PluginId), reportedAt = a.ReportedAt.UtcDateTime, a.PercentageDifference })
    };

    private static string? Text(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string State(JsonElement entry) =>
        Enum.TryParse<PluginState>(Text(entry, "state"), true, out var state) && Enum.IsDefined(state) ? state.ToString() : "Unknown";

    private static object[] Rows(JsonElement rows, Func<JsonElement, object> project) =>
        [.. rows.EnumerateArray().Select(project)];

    private static Dictionary<string, object> Measurements(JsonElement obj, params string[] keys)
    {
        var values = new Dictionary<string, object>();
        foreach (var key in keys)
        {
            if (!obj.TryGetProperty(key, out var item))
            {
                continue;
            }

            values[key] = item.ValueKind switch
            {
                JsonValueKind.Number when item.TryGetDouble(out var n) && double.IsFinite(n) => n,
                JsonValueKind.True or JsonValueKind.False => item.GetBoolean(),
                _ => values[key]
            };
        }

        return values;
    }

    public static object Endpoint(string section, JsonElement data)
    {
        switch (section)
        {
            case "service-version":
                return new { version = VersionNumber(Text(data, "version")) };
            case "service-health":
                return new
                {
                    uptime = Measurements(data, "uptimeSeconds"),
                    plugins = Rows(data.GetProperty("plugins"), p => new { plugin = Plugin(Text(p, "pluginId")), state = State(p) })
                };
            case "plugins":
                return Rows(data.GetProperty("plugins"),
                    p => new
                    {
                        plugin = Plugin(Text(p, "id")),
                        state = State(p),
                        enabled = p.TryGetProperty("enabled", out var flag) && flag.ValueKind == JsonValueKind.True
                    });
            case "nvidia-devices":
            case "astral-devices":
            case "msi-devices":
                return Rows(data, d => new { model = HardwareModel(Text(d, "hardwareName") ?? Text(d, "name"), section) });
            case "msi-device":
                var msi = Measurements(data, "temperatureC", "fanRpm", "automaticFan", "fanDutyPercent", "targetFanDutyPercent");
                msi["model"] = HardwareModel(Text(data, "model"), "msi-devices");

                if (data.TryGetProperty("pinCurrentsA", out var pins))
                {
                    msi["pinCurrentsA"] = pins.EnumerateArray()
                        .Select(row => row.EnumerateArray().Select(pin => pin.GetDouble()).ToArray())
                        .ToArray();
                }

                return msi;
            case "wireview-sensors":
                var wire = Measurements(data, "tempInC", "tempOutC", "extTemp1C", "extTemp2C", "totalCurrentA", "totalPowerW", "psuCapabilityW", "faultStatus", "faultLog");

                if (data.TryGetProperty("pins", out var readings))
                {
                    wire["pins"] = Rows(readings, pin => Measurements(pin, "current", "voltage", "power"));
                }

                return wire;
            case "nvidia-config":
            case "wireview-config":
            case "msi-settings":
                return Measurements(data, "enabled", "automaticallyDisabled", "pollIntervalMs", "staleAfterMs", "fanDutyPercent");
            default: throw new InvalidDataException("Unknown diagnostic section.");
        }
    }

    private static string HardwareModel(string? name, string section)
    {
        if (section == "msi-devices")
        {
            return name?.Contains("Ai1600TS", StringComparison.OrdinalIgnoreCase) == true ? "MSI MPG Ai1600TS"
                : name?.Contains("Ai1300TS", StringComparison.OrdinalIgnoreCase) == true ? "MSI MPG Ai1300TS" : "MSI PSU";
        }

        var brand = section == "astral-devices" ? "ROG Astral" : "NVIDIA";
        var match = GpuModel().Match(name ?? "");

        if (!match.Success)
        {
            return brand + " GPU";
        }

        var family = match.Value.StartsWith("GTX", StringComparison.OrdinalIgnoreCase) ? "GTX" : "RTX";

        return $"{brand} {family} {match.Groups["series"].Value}" + (match.Groups["ti"].Success ? " Ti" : "")
            + (match.Groups["super"].Success ? " SUPER" : "");
    }

    public static string? VersionNumber(string? version) => Version.TryParse(version?.Split('+', '-')[0], out var parsed) ? parsed.ToString() : null;

    public static object Crash(string file, DateTimeOffset timestamp, bool crash)
    {
        var frames = new List<string>();
        var exceptions = new HashSet<string>();

        foreach (var line in File.ReadLines(file).Take(512))
        {
            if (line.Length > 8192)
            {
                continue;
            }

            var text = line.Trim();

            if (text.StartsWith("at RTXLens.", StringComparison.Ordinal) && frames.Count < 20)
            {
                var paren = text.IndexOf('(');
                if (paren < 4)
                {
                    continue;
                }

                var symbol = text[3..paren];
                var dot = symbol.LastIndexOf('.');

                if (dot < 1)
                {
                    continue;
                }

                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name?.StartsWith("RTXLens.", StringComparison.Ordinal) == true))
                {
                    if (assembly.GetType(symbol[..dot]) is not { } type)
                    {
                        continue;
                    }

                    var method = type
                        .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == symbol[(dot + 1)..]);

                    if (method != null)
                    {
                        frames.Add(type.FullName + "." + method.Name);
                    }

                    break;
                }
            }
            else
            {
                var end = text.IndexOf(':');
                var name = (end < 0 ? text : text[..end]).TrimStart('-', '>', ' ');

                if (name.StartsWith("System.", StringComparison.Ordinal) && name.EndsWith("Exception", StringComparison.Ordinal)
                    && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '.') && typeof(Exception).Assembly.GetType(name) is { } type
                    && typeof(Exception).IsAssignableFrom(type))
                {
                    exceptions.Add(type.FullName!);
                }
            }
        }

        return new { timestamp = timestamp.UtcDateTime, kind = crash ? "Crash" : "Action failure", exceptions, frames };
    }
}
