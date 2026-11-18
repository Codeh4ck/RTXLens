using System.Buffers;
using System.Text.Json;
using RTXLens.Contracts.Mobile;

namespace RTXLens.Service.Mobile;

internal sealed class MobileSimulationWarnings(TimeProvider clock)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);
    private const int Capacity = 8;
    private readonly Lock _lock = new();
    private readonly Queue<Warning> _recent = new();

    public void Add(MobileSimulationWarning warning)
    {
        lock (_lock)
        {
            var now = clock.GetUtcNow();
            Expire(now);
            if (_recent.Count == Capacity)
            {
                _recent.Dequeue();
            }

            _recent.Enqueue(new Warning(Guid.NewGuid(), warning, now));
        }
    }

    public byte[] AddToFrame(byte[] frame)
    {
        Warning[] warnings;
        lock (_lock)
        {
            Expire(clock.GetUtcNow());
            if (_recent.Count == 0)
            {
                return frame;
            }

            warnings = _recent.ToArray();
        }

        // Only the paired Debug mobile stream gets this optional field.
        using var snapshot = JsonDocument.Parse(frame);
        var buffer = new ArrayBufferWriter<byte>(frame.Length + 512);
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        foreach (var property in snapshot.RootElement.EnumerateObject())
        {
            property.WriteTo(writer);
        }

        writer.WriteStartArray("simulationWarnings");
        foreach (var warning in warnings)
        {
            writer.WriteStartObject();
            writer.WriteString("id", warning.Id);
            writer.WriteString("title", warning.Message.Title);
            writer.WriteString("detail", warning.Message.Detail);
            writer.WriteString("startedAt", warning.StartedAt);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    public void Clear()
    {
        lock (_lock)
        {
            _recent.Clear();
        }
    }

    private void Expire(DateTimeOffset now)
    {
        while (_recent.TryPeek(out var warning) && now - warning.StartedAt >= Lifetime)
        {
            _recent.Dequeue();
        }
    }

    private sealed record Warning(Guid Id, MobileSimulationWarning Message, DateTimeOffset StartedAt);
}
