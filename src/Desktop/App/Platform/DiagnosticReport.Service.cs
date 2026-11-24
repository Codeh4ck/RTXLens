using System.Net.WebSockets;
using System.Text.Json;
using RTXLens.Client.Http;
using RTXLens.Client.Serialization;
using RTXLens.Contracts.Diagnostics;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Monitoring;
using RTXLens.Contracts.Sensors;

namespace RTXLens.App.Platform;

internal sealed partial class DiagnosticReport
{
    private async Task CollectHistoryAsync(HttpClient http)
    {
        var client = new ServiceLogClient(http);
        try
        {
            await using var writer = File.CreateText(PathFor("history/service-api.jsonl"));

            long before = 0;
            string? instance = null;
            var summary = new HistorySummary();

            while (true)
            {
                var page = await client.ReadHistoryAsync(before, token);
                if (instance != null && page.InstanceId != instance)
                {
                    throw new InvalidDataException("The service restarted during collection.");
                }

                instance = page.InstanceId;
                if (page.Entries.Length == 0)
                {
                    break;
                }

                var oldest = page.Entries[^1].Sequence;
                if (oldest <= 0 || (before != 0 && oldest >= before))
                {
                    throw new InvalidDataException("The service log cursor did not advance.");
                }

                foreach (var entry in page.Entries)
                {
                    if (InRange(entry.Timestamp))
                    {
                        summary.Write(writer, entry);
                    }
                }

                if (oldest <= page.FirstSequence)
                {
                    break;
                }

                before = oldest;
            }

            _notes.AddRange(summary.Describe("Service log API"));
            return;
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            _notes.Add($"Service log API incomplete: {error.GetType().Name}. Trying the local journal.");
        }

        var debug = http.BaseAddress!.Port == ServiceEndpoint.DebugPort;
        var root = Environment.GetFolderPath(debug ? Environment.SpecialFolder.LocalApplicationData : Environment.SpecialFolder.CommonApplicationData);

        TryCollect("Service journal", () =>
        {
            var path = Path.Combine(root, debug ? "RTXLens.Debug" : "RTXLens", "Logs", "events.jsonl");

            using var input = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            using var writer = File.CreateText(PathFor("history/service-journal.jsonl"));

            var invalid = 0;
            var summary = new HistorySummary();

            while (input.ReadLine() is { } line)
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    var entry = JsonSerializer.Deserialize<ServiceLogEntry>(line, Json);
                    if (entry is null)
                    {
                        invalid++;
                    }
                    else if (InRange(entry.Timestamp))
                    {
                        summary.Write(writer, entry);
                    }
                }
                catch (JsonException)
                {
                    invalid++;
                }
            }

            _notes.AddRange(summary.Describe("Local service journal"));

            if (invalid > 0)
            {
                _notes.Add($"Malformed journal entries skipped: {invalid}");
            }
        });
    }

    private sealed class HistorySummary
    {
        private readonly Dictionary<ServiceLogLevel, int> _counts = [];
        private readonly List<string> _messages = [];

        public void Write(TextWriter output, ServiceLogEntry entry)
        {
            output.WriteLine(JsonSerializer.Serialize(SupportReportData.Event(entry), Json));

            _counts[entry.Level] = _counts.GetValueOrDefault(entry.Level) + 1;
            if (entry.Level is ServiceLogLevel.Warning or ServiceLogLevel.Error && _messages.Count < 30)
            {
                _messages.Add($"{entry.Timestamp.UtcDateTime:u} {entry.Level} {SupportReportData.EventCode(entry.Code)}");
            }
        }

        public IEnumerable<string> Describe(string source) =>
            new[] { $"{source}: {_counts.Values.Sum()} events ({string.Join(", ", _counts.Select(c => $"{c.Value} {c.Key}"))})." }.Concat(_messages);
    }

    private async Task CollectLiveAsync(HttpClient http)
    {
        (string route, string file)[] routes =
        [
            ("version", "service-version"), ("health", "service-health"), ("plugins", "plugins"),
            ("plugins/generic-nvidia/config", "nvidia-config"), ("plugins/generic-nvidia/devices", "nvidia-devices"),
            ("plugins/asus-astral/devices", "astral-devices"), ("plugins/msi-psu/devices", "msi-devices"),
            ("plugins/msi-psu/settings", "msi-settings"), ("plugins/msi-psu/device", "msi-device"),
            ("plugins/wireview-pro2/config", "wireview-config"), ("plugins/wireview-pro2/sensors", "wireview-sensors")
        ];

        var results = await Task.WhenAll(routes.Select(async item =>
        {
            try
            {
                using var response = await http.GetAsync(item.route, token);

                if (response.IsSuccessStatusCode)
                {
                    using var data = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));

                    var section = SupportReportData.Endpoint(item.file, data.RootElement);
                    await File.WriteAllTextAsync(PathFor("current/" + item.file + ".json"), JsonSerializer.Serialize(section, Json), token);
                }

                return $"GET {item.route}: HTTP {(int)response.StatusCode}";
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                return $"GET {item.route} unavailable: {error.GetType().Name}";
            }
        }));

        _notes.AddRange(results);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));

        using var socket = new ClientWebSocket();
        socket.Options.Proxy = null;

        var uri = new UriBuilder(new Uri(http.BaseAddress!, ServiceRoutes.Sensors))
        {
            Scheme = http.BaseAddress!.Scheme == "https" ? "wss" : "ws", Port = http.BaseAddress.Port
        };

        var count = 0;
        var invalidFrames = 0;
        SensorSnapshot? latest = null;

        try
        {
            await socket.ConnectAsync(uri.Uri, deadline.Token);

            var buffer = new byte[16 * 1024];
            using var frame = new MemoryStream();

            while (count < 5)
            {
                frame.SetLength(0);
                ValueWebSocketReceiveResult received;

                do
                {
                    received = await socket.ReceiveAsync(buffer.AsMemory(), deadline.Token);
                    if (received.MessageType != WebSocketMessageType.Text || frame.Length + received.Count > 2 * 1024 * 1024)
                    {
                        throw new InvalidDataException("The service closed the stream or sent an unsupported sensor frame.");
                    }

                    frame.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);

                var bytes = frame.GetBuffer().AsMemory(0, (int)frame.Length);
                SensorSnapshot snapshot;
                string safeFrame;

                try
                {
                    snapshot = JsonSerializer.Deserialize(bytes.Span, ClientJsonContext.Default.SensorSnapshot) ?? throw new JsonException("Missing snapshot.");
                    safeFrame = JsonSerializer.Serialize(SupportReportData.Snapshot(snapshot), Json);
                }
                catch (Exception error) when (error is JsonException or ArgumentException or NullReferenceException)
                {
                    invalidFrames++;
                    continue;
                }

                await File.WriteAllTextAsync(PathFor($"live/snapshot-{count + 1:D3}.json"), safeFrame, token);

                latest = snapshot;
                count++;
            }

            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Capture complete", deadline.Token);
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            _notes.Add(deadline.IsCancellationRequested ? "Live readings timed out after 20 seconds." : $"Live readings unavailable: {error.GetType().Name}");
        }
        if (invalidFrames > 0)
        {
            _notes.Add($"Malformed live sensor frames skipped: {invalidFrames}.");
        }

        _notes.Add($"Live sensor frames collected: {count}/5.");

        var readings = (latest?.Sensors ?? []).Where(s => s.Identity is
        {
            PluginId: "generic-nvidia" or "wireview-pro2", Component: SensorComponent.GpuConnector, Channel: 1,
            Metric: SensorMetric.Power or SensorMetric.Current or SensorMetric.Voltage
        });

        await Task.WhenAll
        (
            File.WriteAllTextAsync(PathFor("live/electrical-readings.json"), JsonSerializer.Serialize(readings.Select(SupportReportData.Reading), Json), deadline.Token),
            File.WriteAllTextAsync(PathFor("live/electrical-sources.json"), JsonSerializer.Serialize(SupportReportData.Sources(latest), Json), deadline.Token)
        );
    }
}
