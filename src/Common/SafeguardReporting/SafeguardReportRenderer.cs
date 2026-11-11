using System.Globalization;
using System.Net;
using System.Text;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Plugins.Msi.Contracts.Safeguard;

namespace RTXLens.SafeguardReporting;

internal static class SafeguardReportRenderer
{
    private const string Background = "#090e17";
    private const string Panel = "#111927";
    private const string Border = "#29384d";
    private const string Text = "#e6eefb";
    private const string Muted = "#abbcd0";
    private const string Accent = "#39eed1";

    public static (string Html, string Plain) Render(SafeguardReport report)
    {
        var script = SafeguardScripts.All.FirstOrDefault(candidate => candidate.Id == report.ScriptId);
        var simulation = script?.IsSimulation == true;
        var telemetry = report.Telemetry;
        var title = simulation ? "Safeguard+ simulation" : "Safeguard+ execution report";
        var html = new StringBuilder($"""
            <!doctype html>
            <html lang="en">
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'">
                <title>{Encode(title)}</title>
            </head>
            <body style="margin:0;background:{Background};color:{Text};font:14px 'Segoe UI',Arial,sans-serif;">
                <table role="presentation" width="100%" cellspacing="0" cellpadding="0" bgcolor="{Background}">
                    <tr><td align="center" style="padding:20px 12px;">
                        <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="max-width:720px;">
            """);

        var plain = new StringBuilder();

        if (report.IsTestEmail)
        {
            Paragraph("TEST EMAIL. This report uses live sensor readings from the time the test was requested. No Safeguard+ action ran.", "#ffd15c");
        }

        html.Append($"""
                            <tr><td style="padding:12px 4px 18px;border-bottom:1px solid {Border};">
                                <p style="margin:0 0 6px;color:{Accent};font-size:12px;font-weight:bold;">RTX LENS</p>
                                <h1 style="margin:0;color:{Text};font-size:24px;">{Encode(title)}</h1>
                                <p style="margin:6px 0 0;color:{Muted};">{Encode(report.MachineName)}</p>
                            </td></tr>
            """);

        plain.AppendLine("RTX Lens").AppendLine(title).AppendLine(report.MachineName);

        if (simulation && !report.IsTestEmail)
        {
            Paragraph("Validation only. The low-power action and shutdown were simulated.", "#ffd15c");
        }

        AddPanel("Execution", null, [
            (report.IsTestEmail ? "Requested" : "Triggered", FormatTime(report.StartedAt)),
            ("Connector", report.Connector?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable"),
            ("Script", script?.Name ?? report.ScriptId),
            ("Script version", report.ScriptVersion?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable"),
            (simulation ? "Simulated shutdown delay" : "Shutdown delay", report.DelayMs is { } delay ? $"{delay} ms" : "Unavailable"),
            ("Status", report.Status),
            ("Finished", report.FinishedAt is { } finished ? FormatTime(finished) : "-"),
            (report.IsTestEmail ? "Test ID" : "Execution ID", report.Id.ToString("N"))
        ]);

        AddPanel("Execution steps", null, report.Steps.Length == 0
            ? [("Status", "No execution steps were recorded.")]
            : report.Steps.Select(step => (step.Name, $"{FormatTime(step.Timestamp)} · {step.Result}" + (step.ExitCode is { } code ? $" (exit code {code})" : ""))));

        if (report.MailSettings is { } mail)
        {
            AddPanel("Email delivery", null, [
                ("SMTP server", $"{mail.Host}:{mail.Port} ({mail.Security})"),
                ("Sender", mail.Sender),
                ("Recipients", string.Join(", ", mail.Recipients)),
                ("Authentication", mail.Authenticate ? "Configured" : "None"),
                ("Status", report.Delivery?.Status ?? "Pending"),
                ("Details", report.Delivery?.Detail ?? "-")
            ]);
        }

        html.Append($"""<tr><td style="padding:20px 4px 0;"><h2 style="margin:0;color:{Text};font-size:19px;">Sensor snapshot</h2></td></tr>""");

        plain.AppendLine().AppendLine("Sensor snapshot");

        if (telemetry is not { Readings.Length: > 0 })
        {
            Paragraph("No sensor readings were captured at or before this trigger.", Muted);
        }
        else
        {
            Paragraph($"Captured {FormatTime(telemetry.CapturedAt)}. Pin numbers follow each device's reported order.", Muted);

            foreach (var device in telemetry.Readings.GroupBy(reading => (reading.Identity.PluginId, reading.Identity.DeviceId)))
            {
                var source = SourceName(device.Key.PluginId);
                var observed = device.Max(reading => reading.ObservedAt);
                var subtitle = $"{device.Key.DeviceId} · Last sample {FormatTime(observed)}";

                var general = device
                    .Where(reading => !IsPin(reading) && !reading.Identity.IsGpuDiagnostic())
                    .OrderBy(reading => reading.Identity.Component)
                    .ThenBy(reading => reading.Identity.Channel)
                    .ThenBy(reading => reading.Identity.Metric)
                    .ToArray();

                if (general.Length > 0)
                {
                    AddPanel(source, subtitle, general.Select(reading => (Label(reading.Identity), FormatValue(reading))));
                }

                var connectors = device.Where(IsPin).GroupBy(reading => reading.Identity.ConnectorId);

                foreach (var connector in connectors)
                {
                    var heading = connector.Key is null ? $"{source} · Pins" : $"{source} · Connector {connector.Key} pins";
                    var activeProfile = telemetry.SettingsProfile;
                    var profile = activeProfile?.AppliesToConnector(device.Key.PluginId, device.Key.DeviceId) == true ? activeProfile.Profile : null;

                    var cableRating = general
                        .FirstOrDefault(reading => reading is { Identity.Metric: SensorMetric.PowerCapability, Quality: ReadingQuality.Valid, Value: > 0 })?.Value;

                    foreach (var group in connector
                                 .GroupBy(reading => reading.Identity.Metric)
                                 .OrderBy(group => group.Key switch
                                 {
                                     SensorMetric.Current => 0,
                                     SensorMetric.Power => 1,
                                     SensorMetric.Voltage => 2,
                                     _ => 3
                                 }))
                    {
                        var readings = group.OrderBy(reading => reading.Identity.Channel).ToArray();

                        if (PinScale(group.Key, profile, cableRating) is { } scale)
                        {
                            AddPinChart(heading, subtitle, scale, readings);
                        }
                        else
                        {
                            AddPanel(heading, subtitle, readings.Select(reading => (Label(reading.Identity), FormatValue(reading))));
                        }
                    }
                }
            }
        }

        html.Append("</table></td></tr></table></body></html>");

        return (html.ToString(), plain.ToString());

        void Paragraph(string value, string color)
        {
            html.Append($"""<tr><td style="padding:10px 4px 0;color:{color};">{Encode(value)}</td></tr>""");
            plain.AppendLine(value);
        }

        void AddPinChart(string heading, string subtitle, (string Label, string Unit, double Min, double Max) scale, SensorReading[] pins)
        {
            const int height = 88;
            var chartTitle = $"{heading} · {scale.Label}";

            OpenPanel(chartTitle, subtitle);

            html.Append($"""
                <tr><td colspan="2" style="padding:0 12px 12px;">
                    <p style="margin:0 0 8px;color:{Muted};font-size:12px;">Scale {scale.Min.ToString("0.##", CultureInfo.InvariantCulture)}
                        to {scale.Max.ToString("0.##", CultureInfo.InvariantCulture)} {scale.Unit}</p>
                    <table width="100%" cellspacing="0" cellpadding="0" role="presentation"><tr>
                """);

            foreach (var pin in pins)
            {
                var label = $"P{pin.Identity.Channel}";
                var value = FormatValue(pin);
                var fill = 0;

                if (pin is { Quality: ReadingQuality.Valid, Value: { } number } && double.IsFinite(number) && number > scale.Min)
                {
                    fill = Math.Max(1, (int)Math.Round(height * Math.Clamp((number - scale.Min) / (scale.Max - scale.Min), 0, 1)));
                }

                html.Append($"""
                    <td width="{100 / pins.Length}%" align="center" valign="bottom" style="padding:0 3px;">
                        <div style="margin-bottom:4px;color:{Text};font-size:12px;font-weight:bold;">{Encode(value)}</div>
                        <table width="70%" cellspacing="0" cellpadding="0" role="presentation" bgcolor="{Border}"
                            style="background:{Border};margin:0 auto;">
                    """);

                if (height > fill)
                {
                    html.Append($"""<tr><td height="{height - fill}" style="font-size:0;line-height:0;">&nbsp;</td></tr>""");
                }
                if (fill > 0)
                {
                    html.Append($"""<tr><td height="{fill}" bgcolor="{Accent}" style="background:{Accent};font-size:0;line-height:0;">&nbsp;</td></tr>""");
                }

                html.Append($"""</table><div style="margin-top:4px;color:{Muted};font-size:12px;">{Encode(label)}</div></td>""");
                plain.Append(label).Append(": ").AppendLine(value);
            }

            html.Append("</tr></table></td></tr></table></td></tr>");
        }

        void OpenPanel(string heading, string? subtitle)
        {
            html.Append($"""
                <tr><td style="padding-top:12px;">
                    <table width="100%" cellspacing="0" cellpadding="0" bgcolor="{Panel}"
                        style="border:1px solid {Border};border-radius:8px;border-collapse:separate;">
                        <tr><td colspan="2" style="padding:12px;color:{Accent};">
                            <h2 style="margin:0;font-size:16px;">{Encode(heading)}</h2>
                """);

            plain.AppendLine().AppendLine(heading);

            if (subtitle is not null)
            {
                html.Append($"""<p style="margin:5px 0 0;color:{Muted};font-size:12px;overflow-wrap:anywhere;">{Encode(subtitle)}</p>""");
                plain.AppendLine(subtitle);
            }

            html.Append("</td></tr>");
        }

        void AddPanel(string heading, string? subtitle, IEnumerable<(string Label, string Value)> rows)
        {
            OpenPanel(heading, subtitle);

            foreach (var (label, value) in rows)
            {
                html.Append($"""
                    <tr>
                        <th scope="row" align="left" style="padding:6px 12px;border-top:1px solid {Border};color:{Muted};font-weight:normal;">{Encode(label)}</th>
                        <td align="right" style="padding:6px 12px;border-top:1px solid {Border};color:{Text};font-weight:bold;overflow-wrap:anywhere;">{Encode(value)}</td>
                    </tr>
                    """);

                plain.Append(label).Append(": ").AppendLine(value);
            }

            html.Append("</table></td></tr>");
        }
    }

    private static (string Label, string Unit, double Min, double Max)? PinScale(SensorMetric metric, SettingsProfile? profile, double? cableRating) => metric switch
    {
        SensorMetric.Current => ("Current", "A", 0, profile?.CurrentCeiling ?? 10),
        SensorMetric.Power => ("Power", "W", 0, profile?.ResolvePowerCeiling(cableRating) ?? (cableRating ?? 600) / 6),
        SensorMetric.Voltage => ("Voltage", "V", profile?.VoltageMinimum ?? 11.4, profile?.VoltageMaximum ?? 12.6),
        _ => null
    };

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private static string FormatTime(DateTimeOffset time) => time.ToLocalTime()
        .ToString("ddd, dd MMM yyyy 'at' HH:mm:ss '(UTC'zzz')'", CultureInfo.InvariantCulture);

    private static string SourceName(string id) => id switch
    {
        "asus-astral" => "ROG Astral",
        "wireview-pro2" => "WireView Pro 2",
        "msi-psu" => "MSI PSU",
        "generic-nvidia" => "NVIDIA GPU status",
        _ => id
    };

    private static bool IsPin(SensorReading reading) => reading.Identity.Component is SensorComponent.GpuPin or SensorComponent.PsuPin;

    private static string Label(SensorIdentity identity)
    {
        var component = identity.Component switch
        {
            SensorComponent.GpuBoard => "GPU board",
            SensorComponent.GpuCore => "Core",
            SensorComponent.GpuMemory => "VRAM",
            SensorComponent.GpuHotspot => "Hotspot",
            SensorComponent.GpuFan => $"Fan {identity.Channel}",
            SensorComponent.GpuConnector or SensorComponent.PsuConnector => $"Connector {identity.ConnectorId ?? identity.Channel?.ToString()}",
            SensorComponent.GpuPin or SensorComponent.PsuPin => $"P{identity.Channel}",
            SensorComponent.MonitorBoard => identity.Channel switch { 1 => "Board in", 2 => "Board out", _ => "WireView" },
            SensorComponent.ExternalProbe => $"External probe {identity.Channel}",
            SensorComponent.Psu => "PSU",
            _ => identity.Component.ToString()
        };

        var metric = identity.Metric switch
        {
            SensorMetric.FanSpeed => identity.Component == SensorComponent.Psu ? "fan speed" : "speed",
            SensorMetric.FanDuty => "fan duty",
            SensorMetric.FanAutomatic => "automatic fan",
            SensorMetric.MemoryUsed => "used",
            SensorMetric.MemoryFree => "free",
            SensorMetric.MemoryTotal => "total",
            SensorMetric.PowerCapability => "cable rating",
            SensorMetric.FaultStatus => "fault status",
            SensorMetric.FaultLog => "fault log",
            SensorMetric.ImbalancePercentage => "current imbalance",
            SensorMetric.ImbalanceDetected => "imbalance detected",
            SensorMetric.OvercurrentDetected => "overcurrent detected",
            _ => identity.Metric.ToString().ToLowerInvariant()
        };

        return $"{component.TrimEnd()} {metric}";
    }

    private static string FormatValue(SensorReading reading)
    {
        if (reading.Quality == ReadingQuality.Stale)
        {
            return "Stale";
        }

        if (reading.Quality != ReadingQuality.Valid || reading.Value is not { } value || !double.IsFinite(value))
        {
            return "Unavailable";
        }

        switch (reading.Unit)
        {
            case SensorUnit.Boolean:
                return value == 0 ? "No" : "Yes";
            case SensorUnit.Bitmask:
                return value == 0 ? "Clear" : $"0x{(long)value:X}";
        }

        var (number, format, unit) = reading.Unit switch
        {
            SensorUnit.Watts => (value, "0.##", "W"),
            SensorUnit.Amperes => (value, "0.00", "A"),
            SensorUnit.Volts when reading.Identity.Component == SensorComponent.GpuCore => (value * 1000, "0.0", "mV"),
            SensorUnit.Volts => (value, "0.00", "V"),
            SensorUnit.Celsius => (value, "0.0", "°C"),
            SensorUnit.Megahertz => (value, "0", "MHz"),
            SensorUnit.Rpm => (value, "0", "RPM"),
            SensorUnit.Percent => (value, "0.#", "%"),
            SensorUnit.Megabytes => (value / 1024, "0.00", "GB"),
            _ => (value, "0.##", "")
        };

        var text = number.ToString(format, CultureInfo.InvariantCulture);
        return $"{text} {unit}".TrimEnd() + (reading.Derived ? " (derived)" : "");
    }
}
