using RTXLens.Plugins.Asus.Contracts.Warnings;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RTXLens.Client.Serialization;
using RTXLens.Client.Streaming;
using RTXLens.Contracts.Warnings;
using RTXLens.Tests.Infrastructure;
using RTXLens.UI.Tests.Fixtures;
using RTXLens.UI.Tests.TestDoubles;

namespace RTXLens.UI.Tests.Streaming;

internal static class FrameReaderTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync(ReassemblesFragmentsAndAcknowledgesCloseAsync);
        await runner.RunAsync(RejectsBinaryMessagesAsync);
        await runner.RunAsync(RejectsUnsupportedSchemasAsync);
        await runner.RunAsync(RejectsOversizedFramesAsync);
        await runner.RunAsync(RejectsMalformedWarningStatesAsync);
    }

    private static async Task ReassemblesFragmentsAndAcknowledgesCloseAsync()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(UiTestData.Frame(), ClientJsonContext.Default.SensorSnapshot);
        using var socket = new FakeWebSocket([new SocketPacket(bytes[..30], WebSocketMessageType.Text, false),
            new SocketPacket(bytes[30..], WebSocketMessageType.Text)]);
        var frames = 0;
        await foreach (var frame in SensorFrameReader.ReadAsync(socket, CancellationToken.None))
        {
            frames++;
            TestAssert.Equal(5d, frame.Sensors.Single().Value);
        }
        TestAssert.Equal(1, frames);
        TestAssert.True(socket.CloseAcknowledged, "Peer close must be acknowledged.");
    }

    private static Task RejectsBinaryMessagesAsync() => RejectAsync([new SocketPacket([1], WebSocketMessageType.Binary)]);

    private static Task RejectsUnsupportedSchemasAsync() => RejectAsync([new SocketPacket(
        Encoding.UTF8.GetBytes("{\"schemaVersion\":2,\"sensors\":[],\"plugins\":[],\"selections\":[]}"), WebSocketMessageType.Text)]);

    private static Task RejectsOversizedFramesAsync() => RejectAsync(Enumerable.Range(0, 129)
        .Select(_ => new SocketPacket(new byte[8192], WebSocketMessageType.Text, false)));

    private static async Task RejectsMalformedWarningStatesAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new WarningState("core", "asus-astral", "gpu0",
            AsusWarningIds.CoreTemperature, "Core", "81 °C",
            WarningPhase.Active, "episode", now, new WarningPolicy(), 1, now);
        foreach (var feed in new WarningSnapshot[]
        {
            new("server", 1, true, [null!]),
            new("server", 1, true, [state, state]),
            new("server", 1, true, [state with { NotificationSequence = 2 }]),
            new("server", 1, true, [state with { Policy = new WarningPolicy { RepeatIntervalSeconds = 1 } }])
        })
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(UiTestData.Frame() with { Warnings = feed }, ClientJsonContext.Default.SensorSnapshot);
            await RejectAsync([new SocketPacket(bytes, WebSocketMessageType.Text)]);
        }
    }

    private static async Task RejectAsync(IEnumerable<SocketPacket> packets)
    {
        using var socket = new FakeWebSocket(packets);
        try
        {
            await foreach (var _ in SensorFrameReader.ReadAsync(socket, CancellationToken.None)) { }
        }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid frames must be rejected before reaching view models.");
    }
}
