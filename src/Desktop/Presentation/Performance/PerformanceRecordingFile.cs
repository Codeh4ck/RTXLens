
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RTXLens.Presentation.Performance;

public static class PerformanceRecordingFile
{
    public const int MaximumFileBytes = 256 * 1024 * 1024;

    public static string Serialize(PerformanceChartFrame session)
    {
        Validate(session);
        return JsonSerializer.Serialize(new PerformanceRecordingDocument(1, session),
            PerformanceChartJsonContext.Default.PerformanceRecordingDocument);
    }

    public static PerformanceChartFrame Deserialize(string contents)
    {
        if (string.IsNullOrWhiteSpace(contents) || contents.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("Select a recording file smaller than 256 MB.");
        }

        var json = contents.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        try
        {
            if (json.StartsWith('<'))
            {
                var match = Regex.Match(json, """<script\b[^>]*\bid\s*=\s*(["'])session-data\1[^>]*>(?<data>[\s\S]*?)</script\s*>""",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
                if (!match.Success)
                {
                    throw new InvalidDataException("The HTML file does not contain an RTX Lens recording.");
                }

                json = match.Groups["data"].Value;
            }

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The file does not contain an RTX Lens recording.");
            }

            PerformanceChartFrame? session;
            if (root.TryGetProperty("schemaVersion", out var version))
            {
                if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schemaVersion) || schemaVersion != 1)
                {
                    throw new InvalidDataException("This recording file version is unsupported.");
                }

                session = JsonSerializer.Deserialize(json, PerformanceChartJsonContext.Default.PerformanceRecordingDocument)?.Session;
            }
            else
            {
                // Earlier HTML exports embedded the completed session directly.
                session = JsonSerializer.Deserialize(json, PerformanceChartJsonContext.Default.PerformanceChartFrame);
            }

            Validate(session);
            return session!;
        }
        catch (RegexMatchTimeoutException error)
        {
            throw new InvalidDataException("The HTML recording could not be read.", error);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The recording file contains invalid data.", error);
        }
    }

    public static void Validate(PerformanceChartFrame? session)
    {
        if (session is not { DisplayOnly: false, Recording: false, StartedAt: not null, Series: { Length: > 0 and <= 256 }, SampleCount: > 0 and <= 2_000_000 }
            || !double.IsFinite(session.DurationSeconds) || session.DurationSeconds < 0
            || session.DurationSeconds >= TimeSpan.MaxValue.TotalSeconds)
        {
            throw new InvalidDataException("Select a completed RTX Lens recording containing captured readings.");
        }

        if (session.GpuDetails is not { Length: <= 256 } details
            || details.Any(detail => detail is null || string.IsNullOrWhiteSpace(detail.DeviceId) || detail.DeviceId.Length > 512
                || !PerformanceGpuDetail.Supports(detail.Metric) || !double.IsFinite(detail.Value) || detail.Value < 0)
            || details.DistinctBy(detail => (detail.DeviceId, detail.Metric)).Count() != details.Length)
        {
            throw new InvalidDataException("The recording contains invalid GPU capabilities.");
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        long count = 0;
        long points = 0;
        foreach (var series in session.Series)
        {
            if (series?.Trace is not { Identity: { } identity } trace
                || series.Samples is null || series.Offset != 0 || series.Count != series.Samples.Length
                || string.IsNullOrWhiteSpace(trace.Label) || trace.Label.Length > 512
                || string.IsNullOrWhiteSpace(trace.Source) || trace.Source.Length > 256
                || string.IsNullOrWhiteSpace(identity.PluginId) || identity.PluginId.Length > 256
                || string.IsNullOrWhiteSpace(identity.DeviceId) || identity.DeviceId.Length > 512
                || identity.ConnectorId?.Length > 128 || trace.DeviceId != identity.DeviceId
                || trace.Key != identity.Key || !keys.Add(trace.Key)
                || trace.Color is not { Length: 7 } color || color[0] != '#' || !color.Skip(1).All(char.IsAsciiHexDigit))
            {
                throw new InvalidDataException("The recording contains an invalid trace.");
            }

            var definition = PerformanceTrace.Create(new(identity, Contracts.Sensors.SensorUnits.For(identity.Metric), 0,
                session.StartedAt.Value), trace.Source, 0);
            if (definition is null || definition.Group != trace.Group || definition.Unit != trace.Unit)
            {
                throw new InvalidDataException("The recording contains an unsupported metric or unit.");
            }

            var previous = -1d;
            foreach (var sample in series.Samples)
            {
                points++;
                if (points > 4_000_000 || !double.IsFinite(sample.Seconds) || sample.Seconds < 0
                    || sample.Seconds < previous || sample.Seconds > session.DurationSeconds + 0.1
                    || sample.Value is { } value && !double.IsFinite(value))
                {
                    throw new InvalidDataException("The recording contains invalid or excessive samples.");
                }

                previous = sample.Seconds;
                if (sample.Value is not null)
                {
                    count++;
                }
            }
        }

        if (count != session.SampleCount)
        {
            throw new InvalidDataException("The recording sample count is invalid.");
        }
    }

}
