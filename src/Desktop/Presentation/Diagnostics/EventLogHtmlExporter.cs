using System.Globalization;
using System.Net;
using RTXLens.Contracts.Diagnostics;

namespace RTXLens.Presentation.Diagnostics;

public static class EventLogHtmlExporter
{
    private const string DocumentStart = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'">
        <title>RTX Lens - Event Log</title>
        <style>
        :root {
            color-scheme: dark;
            --background: #090e17;
            --panel: #111927;
            --border: #29384d;
            --text: #e6eefb;
            --muted: #abbcd0;
            --accent: #39eed1;
            --warning: #ffc564;
            --error: #ff819a;
        }
        body.light {
            color-scheme: light;
            --background: #f3f6fa;
            --panel: #fff;
            --border: #d1dce9;
            --text: #172235;
            --muted: #52647b;
            --accent: #007f76;
            --warning: #825000;
            --error: #ac2445;
        }
        * { box-sizing: border-box; }
        body { margin: 0; background: var(--background); color: var(--text); font: 14px "Segoe UI", sans-serif; }
        header { padding: 16px; border-bottom: 1px solid var(--border); }
        h1 { margin: 0 0 6px; font-size: 22px; }
        header p { color: var(--muted); margin: 4px 0; }
        main { padding: 12px; }
        section { background: var(--panel); border: 1px solid var(--border); border-radius: 8px; overflow-x: auto; }
        table { width: 100%; min-width: 760px; border-collapse: collapse; line-height: 1.45; }
        th, td { padding: 9px 12px; text-align: left; vertical-align: top; border-bottom: 1px solid var(--border); }
        th { color: var(--muted); background: var(--background); font-weight: 600; }
        td { overflow-wrap: anywhere; }
        tr:last-child td { border-bottom: 0; }
        time { color: var(--muted); white-space: nowrap; font-variant-numeric: tabular-nums; }
        .level { font-weight: 600; }
        .information { color: var(--accent); }
        .warning { color: var(--warning); }
        .error { color: var(--error); }
        .message { min-width: 16ch; white-space: pre-wrap; }
        .details { min-width: 16ch; color: var(--muted); white-space: pre-wrap; }
        .code { color: var(--muted); }
        .empty { padding: 24px; text-align: center; color: var(--muted); }
        </style>
        </head>
        """;

    public static IEnumerable<string> Lines(IEnumerable<ServiceLogEntry> entries, bool complete, bool lightTheme)
    {
        yield return DocumentStart;
        yield return lightTheme ? """<body class="light">""" : "<body>";

        var coverage = complete
            ? "Retained backend events and desktop configuration changes"
            : "Loaded backend events and desktop configuration changes";
        yield return $"<header><h1>RTX Lens - Event Log</h1><p>{coverage}</p></header>";
        yield return """
            <main>
            <section aria-label="Exported events" tabindex="0">
            <table aria-label="RTX Lens events">
            <thead><tr><th scope="col">Time</th><th scope="col">Level</th><th scope="col">Source</th><th scope="col">Message</th><th scope="col">Details</th><th scope="col">Event code</th></tr></thead>
            <tbody>
            """;

        var hasEvents = false;

        foreach (var entry in entries)
        {
            hasEvents = true;
            var timestamp = entry.Timestamp.ToLocalTime();
            var timestampAttribute = timestamp.ToString("O", CultureInfo.InvariantCulture);
            var timestampLabel = timestamp.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
            var levelClass = entry.Level switch
            {
                ServiceLogLevel.Information => "information",
                ServiceLogLevel.Warning => "warning",
                ServiceLogLevel.Error => "error",
                _ => string.Empty
            };

            yield return $"""
                <tr>
                <td><time datetime="{Encode(timestampAttribute)}">{Encode(timestampLabel)}</time></td>
                <td class="level {levelClass}">{Encode(entry.Level.ToString())}</td>
                <td>{Encode(entry.Source)}</td>
                <td class="message">{Encode(entry.Message)}</td>
                <td class="details">{Encode(entry.Detail)}</td>
                <td class="code">{Encode(entry.Code)}</td>
                </tr>
                """;
        }

        if (!hasEvents)
        {
            yield return """<tr><td class="empty" colspan="6">No events to display.</td></tr>""";
        }

        yield return "</tbody></table></section></main></body></html>";
    }

    private static string Encode(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
}
