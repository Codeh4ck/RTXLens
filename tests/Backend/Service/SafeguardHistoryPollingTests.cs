using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RTXLens.Plugins.Msi.Contracts.Safeguard;
using RTXLens.Service.Services;
using RTXLens.Tests.Infrastructure;
using RTXLens.Tests.TestDoubles;

namespace RTXLens.Tests.Service;

[SupportedOSPlatform("windows")]
internal static class SafeguardHistoryPollingTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync(CompletedHistoryIsNotRereadWhileNewExecutionsAppearAsync);
        await runner.RunAsync(ChangedCompletedHistoryIsRefreshedAsync);
        await runner.RunAsync(ActiveHistoryAdvancesAndExpiredNoticesDisappearAsync);
        await runner.RunAsync(MailJobsArePromptWithoutScanningCleanupAreasEverySecondAsync);
    }

    private static async Task CompletedHistoryIsNotRereadWhileNewExecutionsAppearAsync()
    {
        using var files = new JournalFiles();
        using var journal = files.CreateJournal();
        var completed = files.Add(finished: true);
        await journal.RefreshNoticesAsync(files, default);
        var active = files.Add(finished: false);
        await journal.RefreshNoticesAsync(files, default);
        TestAssert.True(journal.Notices!.Any(n => n.Id == active.Id), "A new execution must appear on the next poll.");
        TestAssert.True(journal.Notices!.Any(n => n.Id == completed.Id), "The completed execution must remain visible.");
        TestAssert.Equal(1, files.HistoryReads[completed.Id]);
    }

    private static async Task ChangedCompletedHistoryIsRefreshedAsync()
    {
        using var files = new JournalFiles();
        using var journal = files.CreateJournal();
        var completed = files.Add(finished: true);
        await journal.RefreshNoticesAsync(files, default);
        files.Clock.Advance(TimeSpan.FromSeconds(2));
        files.Write("history", completed.Id, completed with { Status = "Corrected" });
        await journal.RefreshNoticesAsync(files, default);
        TestAssert.Equal("Corrected", journal.Notices!.Single().Status);
        files.Delete("history", completed.Id);
        await journal.RefreshNoticesAsync(files, default);
        TestAssert.Equal(0, journal.Notices!.Length);
    }

    private static async Task ActiveHistoryAdvancesAndExpiredNoticesDisappearAsync()
    {
        using var files = new JournalFiles();
        using var journal = files.CreateJournal();
        var active = files.Add(finished: false);
        await journal.RefreshNoticesAsync(files, default);
        TestAssert.Equal("Running", journal.Notices!.Single().Status);
        files.Clock.Advance(TimeSpan.FromMinutes(2));
        await journal.RefreshNoticesAsync(files, default);
        TestAssert.Equal("Interrupted", journal.Notices!.Single().Status);
        files.Write("history", active.Id, active with { FinishedAt = files.Clock.GetUtcNow(), Status = "Simulated" });
        await journal.RefreshNoticesAsync(files, default);
        TestAssert.Equal("Simulated", journal.Notices!.Single().Status);
        files.Clock.Advance(TimeSpan.FromDays(2));
        await journal.RefreshNoticesAsync(files, default);
        TestAssert.Equal(0, journal.Notices!.Length);
        TestAssert.False(File.Exists(files.Location("history", active.Id)), "The expired record must be deleted.");
    }

    private static async Task MailJobsArePromptWithoutScanningCleanupAreasEverySecondAsync()
    {
        using var files = new JournalFiles();
        using var journal = files.CreateJournal();
        await journal.ProcessMailAsync(files, default);
        var oldResult = Guid.NewGuid();
        files.Write("results", oldResult, new { Accepted = true });
        var resultPath = files.Location("results", oldResult);
        File.SetLastWriteTimeUtc(resultPath, files.Clock.GetUtcNow().AddHours(-2).UtcDateTime);
        files.Clock.Advance(TimeSpan.FromSeconds(1));
        var job = new SafeguardMailEvent(Guid.NewGuid(), Guid.NewGuid(), files.Clock.GetUtcNow().AddDays(-8));
        files.Write("outbox", job.ExecutionId, job);
        await journal.ProcessMailAsync(files, default);
        TestAssert.Equal("Delivery expired", files.Read<SafeguardDelivery>("delivery", job.ExecutionId)?.Status);
        TestAssert.False(File.Exists(files.Location("outbox", job.ExecutionId)), "The next poll must consume the expired job.");
        TestAssert.Equal(1, files.Scans["results"]);
        TestAssert.Equal(1, files.Scans["delivery"]);
        TestAssert.True(File.Exists(resultPath), "Idle cleanup must respect its interval.");
        files.Clock.Advance(TimeSpan.FromMinutes(1));
        await journal.ProcessMailAsync(files, default);
        TestAssert.False(File.Exists(resultPath), "The next cleanup must remove the expired result.");
    }

    private sealed class JournalFiles : ISafeguardJournalStorage, IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("RTXLens-safeguard-test-");
        public int RetentionDays => 1;
        public TestTimeProvider Clock { get; } = new();
        public Dictionary<Guid, int> HistoryReads { get; } = [];
        public Dictionary<string, int> Scans { get; } = [];
        public SafeguardJournalService CreateJournal() => new(Clock, NullLogger<SafeguardJournalService>.Instance);

        public SafeguardExecution Add(bool finished)
        {
            var now = Clock.GetUtcNow();
            var record = new SafeguardExecution(Guid.NewGuid(), Guid.NewGuid(), SafeguardScripts.Simulation, 1, 1, 3000,
                now, finished ? now : null, finished ? "Simulated" : "Running", []);
            Write("history", record.Id, record);
            return record;
        }

        public string Location(string area, Guid id) => Path.Combine(_root.FullName, area, $"{id:N}.json");

        public IEnumerable<Guid> Ids(string area)
        {
            Scans[area] = Scans.GetValueOrDefault(area) + 1;
            var directory = _root.CreateSubdirectory(area);
            return directory.GetFiles("*.json").Select(file => Guid.ParseExact(Path.GetFileNameWithoutExtension(file.Name), "N"));
        }

        public T? Read<T>(string area, Guid id) where T : class
        {
            if (area == "history")
            {
                HistoryReads[id] = HistoryReads.GetValueOrDefault(id) + 1;
            }

            var path = Location(area, id);
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null;
        }

        public void Write<T>(string area, Guid id, T value)
        {
            _root.CreateSubdirectory(area);
            var path = Location(area, id);
            File.WriteAllText(path, JsonSerializer.Serialize(value));
            File.SetLastWriteTimeUtc(path, Clock.GetUtcNow().UtcDateTime);
        }

        public void Delete(string area, Guid id)
        {
            var path = Location(area, id);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        public void DeleteExecutionHistory(Guid id) => Delete("history", id);
        public void Dispose() => _root.Delete(recursive: true);
    }
}
