using System.Text.RegularExpressions;
using RTXLens.Contracts.Warnings;
using RTXLens.Plugins.Asus.Contracts;
using RTXLens.Plugins.Msi.Contracts;
using RTXLens.Plugins.WireView.Contracts;

namespace RTXLens.WarningUI;

public sealed record PopupReading(string Label, string Value);

public sealed partial record PopupWarning(string Title, string Source, string Detail, string Context, PopupReading[] Readings)
{
    public bool HasReadings => Readings.Length > 0;

    public static PopupWarning FromState(WarningState state, bool simulation = false)
    {
        var (source, title) = Describe(state.Title, state.PluginId, simulation);
        return Create(title, source, state.Detail, state.Phase);
    }

    public static PopupWarning FromIncident(WarningIncident incident, string source, string title) =>
        Create(title, source, incident.Detail ?? "");

    public static (string Source, string Title) Describe(string title, string pluginId, bool simulation = false)
    {
        const string debugPrefix = "Debug · ";
        if (title.StartsWith(debugPrefix, StringComparison.OrdinalIgnoreCase))
        {
            simulation = true;
            title = title[debugPrefix.Length..];
        }
        var split = title.LastIndexOf(" · ", StringComparison.Ordinal);
        var source = split >= 0 ? title[..split] : pluginId switch
        {
            AsusIdentity.Id => AsusIdentity.DisplayName,
            WireViewIdentity.Id => WireViewIdentity.DisplayName,
            MsiIdentity.Id => MsiIdentity.DisplayName,
            "generic-nvidia" => "NVIDIA",
            _ => pluginId
        };
        if (simulation)
        {
            source = "Debug · " + source;
        }

        return (source, split >= 0 ? title[(split + 3)..] : title);
    }

    internal static PopupWarning Create(string title, string source, string detail, WarningPhase phase = WarningPhase.Active)
    {
        var context = "";
        var profile = detail.IndexOf(" (Profile: ", StringComparison.Ordinal);
        if (profile >= 0 && detail.EndsWith(')'))
        {
            context = detail[(profile + 2)..^1];
            detail = detail[..profile];
        }
        if (phase != WarningPhase.Active)
        {
            var status = phase == WarningPhase.Unavailable ? "Unable to verify" : phase.ToString();
            context = context.Length == 0 ? status : $"{status} · {context}";
        }

        // Only replace the known telemetry format. Keep arbitrary firmware messages intact.
        var temperature = TemperatureDetail().Match(detail);
        if (temperature.Success)
        {
            return new(title, source, "", context, [new("Measured", temperature.Groups[1].Value), new("Threshold", temperature.Groups[2].Value)]);
        }

        var match = PinDetail().Match(detail);
        if (!match.Success)
        {
            return new(title, source, detail, context, []);
        }

        var pins = match.Groups["pin"].Captures;
        var values = match.Groups["value"].Captures;
        var readings = new List<PopupReading>(pins.Count + 1);
        for (var i = 0; i < pins.Count; i++)
        {
            readings.Add(new(pins[i].Value, values[i].Value));
        }

        if (match.Groups["spread"] is { Success: true } spread)
        {
            readings.Add(new("Difference", spread.Value));
        }

        return new(title, source, "", context, [.. readings]);
    }

    [GeneratedRegex(@"^(?:(?<pin>P\d+)(?:\s*:\s*|\s+·\s+)(?<value>-?\d+(?:[.,]\d+)?\s+[AV])(?: / (?=P\d)|(?= - |$)))+(?: - (?<spread>\d+(?:[.,]\d+)?%) difference)?$")]
    private static partial Regex PinDetail();

    [GeneratedRegex(@"^(-?\d+(?:[.,]\d+)? °C) - threshold (-?\d+(?:[.,]\d+)? °C)$")]
    private static partial Regex TemperatureDetail();
}
