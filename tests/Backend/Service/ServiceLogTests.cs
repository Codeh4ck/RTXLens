using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RTXLens.Client.Http;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Diagnostics;
using RTXLens.Core.Configuration;
using RTXLens.Core.Diagnostics;
using RTXLens.Service;
using RTXLens.Service.Configuration;
using RTXLens.Service.Diagnostics;
using RTXLens.Tests.Infrastructure;
using RTXLens.Tests.TestDoubles;

namespace RTXLens.Tests.Service;

internal static class ServiceLogTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        if (OperatingSystem.IsWindows())
        {
            runner.Run(SizeRetentionDoesNotRewriteTheEntireJournalForEachNewEvent);
        }
        await runner.RunAsync(RestartRecoversAndPagesPastATruncatedTailAsync);
        await runner.RunAsync(StreamsOnlyApprovedEventsAndResumesAsync);
        await runner.RunAsync(KeepsHistoryAcrossRestartsAndAppliesRetentionAsync);
        await runner.RunAsync(ClearRemovesSavedEventsAndAdvancesStreamAsync);
    }

    private static void SizeRetentionDoesNotRewriteTheEntireJournalForEachNewEvent()
    {
        var root = Directory.CreateTempSubdirectory("RTXLens-retention-");
        try
        {
            var clock = new TestTimeProvider();
            using var input = new ActivityLoggerProvider(clock);
            using var journal = new ServiceLogJournal(input, new RuntimeConfigurationFile(Path.Combine(root.FullName, "settings.json")),
                new TestConfiguration(), clock, NullLogger<ServiceLogJournal>.Instance);
            var logger = input.CreateLogger("Test");
            var detail = new string('x', 1000);
            void AddEvent() => logger.LogActivity(LogLevel.Information, "test.retention", "Test", "Retention workload.", detail);

            for (var i = 0; i < 1000; i++)
            {
                AddEvent();
            }
            journal.PublishPending();
            TestAssert.True(journal.ReadHistory(0, 1).FirstSequence > 1, "The fixture must reach the configured 1 MiB cap.");

            // The runner executes tests serially. Measuring the process also covers file rotation and compaction.
            using var process = Process.GetCurrentProcess();
            var before = WrittenBytes(process.SafeHandle);
            for (var i = 0; i < 12; i++)
            {
                AddEvent();
                journal.PublishPending();
            }
            journal.PublishPending(flush: true);
            var bytesWritten = WrittenBytes(process.SafeHandle) - before;
            Console.WriteLine($"Journal writes for 12 appends at capacity: {bytesWritten} bytes.");

            var newest = journal.ReadHistory(0, 2);
            var older = journal.ReadHistory(newest.Entries[^1].Sequence, 2);
            TestAssert.Equal(1012L, newest.Entries[0].Sequence);
            TestAssert.Equal(1010L, older.Entries[0].Sequence);
            TestAssert.True(bytesWritten < 2 * 1024 * 1024,
                $"Twelve individual appends at the 1 MiB cap wrote {bytesWritten:N0} bytes; allow one compaction plus appends.");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static ulong WrittenBytes(SafeProcessHandle process)
    {
        if (!GetProcessIoCounters(process, out var counters)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return counters.WriteTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool GetProcessIoCounters(SafeProcessHandle process, out IoCounters counters);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations, WriteOperations, OtherOperations;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    private static async Task RestartRecoversAndPagesPastATruncatedTailAsync()
    {
        var root = Directory.CreateTempSubdirectory("RTXLens-journal-recovery-");
        try
        {
            var clock = new TestTimeProvider();
            var settings = new RuntimeConfigurationFile(Path.Combine(root.FullName, "settings.json"));
            var logs = root.CreateSubdirectory("Logs");
            var path = Path.Combine(logs.FullName, "events.jsonl");
            var entries = Enumerable.Range(1, 3).Select(i => new ServiceLogEntry(i, clock.GetUtcNow(),
                ServiceLogLevel.Information, "test.seed", "Test", "Persisted event.", null));
            await File.WriteAllLinesAsync(path, entries.Select(e => JsonSerializer.Serialize(e, AppJsonContext.Default.ServiceLogEntry)));
            await File.AppendAllTextAsync(path, "{\"sequence\":4,");

            using (var input = new ActivityLoggerProvider(clock))
            using (var journal = new ServiceLogJournal(input, settings, new TestConfiguration(), clock, NullLogger<ServiceLogJournal>.Instance))
            {
                await journal.StartAsync(default);
                try
                {
                    var newest = journal.ReadHistory(0, 2);
                    TestAssert.True(newest.Entries.Select(e => e.Sequence).SequenceEqual([3L, 2L]), "The last two complete records must survive.");
                    TestAssert.Equal(1L, journal.ReadHistory(2, 2).Entries.Single().Sequence);
                    input.CreateLogger("Test").LogActivity(LogLevel.Information, "test.recovered", "Test", "Recovered event.");
                    journal.PublishPending();
                }
                finally
                {
                    await journal.StopAsync(default);
                }
            }

            using var recoveredInput = new ActivityLoggerProvider(clock);
            using var recovered = new ServiceLogJournal(recoveredInput, settings, new TestConfiguration(), clock, NullLogger<ServiceLogJournal>.Instance);
            await recovered.StartAsync(default);
            try
            {
                var entry = recovered.ReadHistory(0, 1).Entries.Single();
                TestAssert.Equal(4L, entry.Sequence);
                TestAssert.Equal("test.recovered", entry.Code);
            }
            finally
            {
                await recovered.StopAsync(default);
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static async Task StreamsOnlyApprovedEventsAndResumesAsync()
    {
        await using var host = await MonitoringTestHost.StartWithTestPluginsAsync();
        var client = new ServiceLogClient(host.Client);
        var logger = host.CreateLogger("ActivityTest");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var token = deadline.Token;

        var initial = await client.ReadHistoryAsync(0, token);
        await using (var stream = client.FollowAsync(initial.InstanceId, initial.LatestSequence, token).GetAsyncEnumerator())
        {
            TestAssert.True(await stream.MoveNextAsync(), "The stream did not send an initial frame.");

            logger.LogWarning("A private diagnostic exception message");
            logger.LogActivity(LogLevel.Warning, "test.timeout", "Test device", "Test device stopped responding.",
                "Will retry.", exception: new IOException("private device path"));

            var entry = await WaitForEventAsync(stream, "test.timeout");
            TestAssert.Equal(ServiceLogLevel.Warning, entry.Level);
            TestAssert.Equal("Test device stopped responding.", entry.Message);
            TestAssert.Equal("Will retry.", entry.Detail);
            initial = stream.Current;
        }

        logger.LogActivity(LogLevel.Information, "test.recovered", "Test device", "Test device readings recovered.");
        await using (var resumed = client.FollowAsync(initial.InstanceId, initial.LatestSequence, token).GetAsyncEnumerator())
        {
            await WaitForEventAsync(resumed, "test.recovered");
        }

        await Task.Delay(TimeSpan.FromSeconds(1.2), token);
        var history = await client.ReadHistoryAsync(0, token);
        TestAssert.True(history.Entries.All(entry => !entry.Message.Contains("private", StringComparison.OrdinalIgnoreCase)),
            "Raw diagnostic records must stay out of consumer history.");
        TestAssert.Equal(1, history.Entries.Count(entry => entry is { Source: "broken", Code: "plugin.poll-failed" }));

        using var crossOrigin = new HttpRequestMessage(HttpMethod.Get, "logs");
        crossOrigin.Headers.Add("Origin", "https://example.test");
        using var response = await host.Client.SendAsync(crossOrigin, token);
        TestAssert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        TestAssert.False(response.Headers.Contains("Access-Control-Allow-Origin"), "Event history must not be readable by browser origins.");
    }

    private static async Task ClearRemovesSavedEventsAndAdvancesStreamAsync()
    {
        await using var host = await MonitoringTestHost.StartWithoutPluginsAsync();
        var client = new ServiceLogClient(host.Client);
        var logger = host.CreateLogger("ActivityTest");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var token = deadline.Token;
        var initial = await client.ReadHistoryAsync(0, token);
        await using var stream = client.FollowAsync(initial.InstanceId, initial.LatestSequence, token).GetAsyncEnumerator();

        const string oldCode = "test.before-clear";
        const string newCode = "test.after-clear";
        logger.LogActivity(LogLevel.Information, oldCode, "Test device", "An event to delete.");
        var oldEvent = await WaitForEventAsync(stream, oldCode);

        var logPath = Path.Combine(Path.GetDirectoryName(host.ConfigurationPath)!, "Logs", "events.jsonl");
        await WaitUntilAsync(() => File.Exists(logPath)
            && File.ReadAllText(logPath).Contains(oldCode, StringComparison.Ordinal));

        using var crossOrigin = new HttpRequestMessage(HttpMethod.Delete, "logs");
        crossOrigin.Headers.Add("Origin", "https://example.test");
        using var forbidden = await host.Client.SendAsync(crossOrigin, token);
        TestAssert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        TestAssert.True((await client.ReadHistoryAsync(0, token)).Entries.Any(entry => entry.Code == oldCode),
            "A rejected request must leave event history intact.");

        var cleared = await client.ClearAsync(token);
        TestAssert.Equal(initial.InstanceId, cleared.InstanceId);
        TestAssert.Equal(0, cleared.Entries.Length);
        TestAssert.True(cleared.FirstSequence > oldEvent.Sequence, "The clear boundary must advance beyond deleted entries.");
        TestAssert.False((await client.ReadHistoryAsync(0, token)).Entries.Any(entry => entry.Code == oldCode),
            "Deleted events must not be returned by history requests.");
        TestAssert.True(!File.Exists(logPath) || !File.ReadAllText(logPath).Contains(oldCode, StringComparison.Ordinal),
            "Cleared events must be removed from the persisted log.");

        while (stream.Current.FirstSequence < cleared.FirstSequence)
        {
            TestAssert.True(await stream.MoveNextAsync(), "The stream did not send the clear boundary.");
        }

        logger.LogActivity(LogLevel.Information, newCode, "Test device", "An event after clearing.");
        var newEvent = await WaitForEventAsync(stream, newCode);
        TestAssert.True(newEvent.Sequence > cleared.LatestSequence, "New events must use a sequence beyond the clear boundary.");
    }

    private static async Task<ServiceLogEntry> WaitForEventAsync(IAsyncEnumerator<ServiceLogPage> stream, string code)
    {
        while (await stream.MoveNextAsync())
        {
            var entry = stream.Current.Entries.FirstOrDefault(item => item.Code == code);
            if (entry is not null)
            {
                return entry;
            }
        }

        throw new InvalidOperationException($"Event '{code}' was not streamed.");
    }

    private static async Task KeepsHistoryAcrossRestartsAndAppliesRetentionAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"RTX-Lens-EventLog-{Guid.NewGuid():N}");
        var journalDirectory = Path.Combine(directory, "Logs");
        Directory.CreateDirectory(journalDirectory);

        try
        {
            var clock = new TestTimeProvider();
            var now = clock.GetUtcNow();
            var configuration = new TestConfiguration();
            var path = new RuntimeConfigurationFile(Path.Combine(directory, "appsettings.json"));
            var logPath = Path.Combine(journalDirectory, "events.jsonl");
            var seed = Enumerable.Range(1, 1200).Select(index => new ServiceLogEntry(index,
                index == 1199 ? now.AddDays(-2) : now, ServiceLogLevel.Information, "test.seed", "Service",
                "A retained event.", new string('x', 950)));

            await File.WriteAllLinesAsync(logPath, seed.Select(entry => JsonSerializer.Serialize(entry, AppJsonContext.Default.ServiceLogEntry)));

            using (var input = new ActivityLoggerProvider(clock))
            using (var journal = new ServiceLogJournal(input, path, configuration, clock, NullLogger<ServiceLogJournal>.Instance))
            {
                await journal.StartAsync(CancellationToken.None);
                await WaitUntilAsync(() => journal.ReadHistory(0, 1).FirstSequence > 1);
                await journal.StopAsync(CancellationToken.None);
            }

            TestAssert.True(new FileInfo(logPath).Length <= 1024 * 1024, "The configured size cap was not applied to the persisted history.");

            using var restartedInput = new ActivityLoggerProvider(clock);
            using var restarted = new ServiceLogJournal(restartedInput, path, configuration, clock, NullLogger<ServiceLogJournal>.Instance);
            await restarted.StartAsync(CancellationToken.None);

            var page = restarted.ReadHistory(0, 250);
            TestAssert.Equal(1200L, page.Entries[0].Sequence);
            TestAssert.True(page.FirstSequence > 1, "Oldest rows should be removed first when storage exceeds its cap.");
            TestAssert.False(page.Entries.Any(entry => entry.Sequence == 1199), "Expired rows must be removed even if they are near the end of the file.");

            restartedInput.CreateLogger("Test").LogActivity(LogLevel.Information, "test.after-restart", "Service", "New event.");
            await WaitUntilAsync(() => restarted.ReadHistory(0, 1).LatestSequence == 1201);
            await restarted.StopAsync(CancellationToken.None);
            TestAssert.Equal(1201L, restarted.ReadHistory(0, 1).LatestSequence);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(25, timeout.Token);
        }
    }

    private sealed class TestConfiguration : IRuntimeConfiguration
    {
        public ConfigurationSnapshot Current { get; } = new(1, new ServiceConfiguration
        {
            Monitoring = new() { EventLogRetentionDays = 1, EventLogStorageMb = 1 }
        });

        public event Action? Changed
        {
            add { }
            remove { }
        }

        public Task<ConfigurationSnapshot> UpdateAsync(Func<ServiceConfiguration, ServiceConfiguration> update,
            CancellationToken cancellationToken, string? expectedVersion = null) => throw new NotSupportedException();
    }
}
