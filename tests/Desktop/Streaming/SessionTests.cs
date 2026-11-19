using RTXLens.Client.Streaming;
using RTXLens.Tests.Infrastructure;
using RTXLens.UI.Tests.Fixtures;
using RTXLens.UI.Tests.TestDoubles;

namespace RTXLens.UI.Tests.Streaming;

internal static class SessionTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync(KeepsOnlyTheLatestFrameAndUsesOneConnectionAsync);
        await runner.RunAsync(RefreshesMetadataWhenTheConfigurationRevisionChangesAsync);
        await runner.RunAsync(ReconnectsAndCancelsCleanlyAsync);
        await runner.RunAsync(RecoversAfterAnIncompatibleFrameAsync);
    }

    private static async Task KeepsOnlyTheLatestFrameAndUsesOneConnectionAsync()
    {
        var api = new FakeMonitoringApi();
        var session = new MonitoringSession(api);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = session.RunAsync(stop.Token);
        for (var sequence = 1; sequence <= 100; sequence++)
        {
            api.Frames.Writer.TryWrite(UiTestData.Frame(sequence: sequence));
        }

        await UntilAsync(() => session.Latest?.Sequence == 100, stop.Token);
        TestAssert.Equal(1, api.StreamConnections);
        stop.Cancel();
        await run;
        TestAssert.Equal<Contracts.Sensors.SensorSnapshot?>(null, session.Latest);
    }

    private static async Task RefreshesMetadataWhenTheConfigurationRevisionChangesAsync()
    {
        var api = new FakeMonitoringApi();
        var session = new MonitoringSession(api);
        session.CatalogReceived += _ => { };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = session.RunAsync(stop.Token);
        api.Configuration = api.Configuration with { Revision = 2, Version = "test:2" };
        api.Frames.Writer.TryWrite(UiTestData.Frame() with { ConfigurationRevision = 2 });
        await UntilAsync(() => session.Latest is not null, stop.Token);
        TestAssert.Equal(2, api.ConfigurationRequests);
        TestAssert.Equal(2, api.CatalogRequests);
        stop.Cancel();
        await run;
    }

    private static async Task ReconnectsAndCancelsCleanlyAsync()
    {
        var api = new FakeMonitoringApi();
        var session = new MonitoringSession(api);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = session.RunAsync(stop.Token);
        session.Reconnect();
        await UntilAsync(() => api.StreamConnections == 2, stop.Token);
        stop.Cancel();
        await run;
    }

    private static async Task RecoversAfterAnIncompatibleFrameAsync()
    {
        var api = new FakeMonitoringApi { FirstStreamError = new InvalidDataException("Unsupported sensor schema.") };
        var session = new MonitoringSession(api);
        var incompatible = false;
        session.StatusChanged += status => incompatible |= status.Message.Contains("Unsupported sensor schema.", StringComparison.Ordinal);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = session.RunAsync(stop.Token);
        api.Frames.Writer.TryWrite(UiTestData.Frame(sequence: 42));
        await UntilAsync(() => session.Latest?.Sequence == 42, stop.Token);
        stop.Cancel();
        await run;
        TestAssert.Equal(2, api.StreamConnections);
        TestAssert.True(incompatible, "The compatibility error must be visible before the session recovers.");
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition())
        {
            await Task.Delay(10, token);
        }
    }
}
