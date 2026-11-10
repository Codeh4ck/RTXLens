using System.Text;
using System.Text.Json;
using RTXLens.Contracts.Diagnostics;
using RTXLens.Contracts.Monitoring;
using RTXLens.Core.Configuration;
using RTXLens.Core.Diagnostics;
using RTXLens.Service.Configuration;

namespace RTXLens.Service.Diagnostics;

internal sealed class ServiceLogJournal(ActivityLoggerProvider input, RuntimeConfigurationFile configurationFile,
    IRuntimeConfiguration configuration, TimeProvider clock, ILogger<ServiceLogJournal> logger,
    ActivityEventLogProvider? eventViewer = null) : BackgroundService
{
    private const int PageSize = 250;
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly Lock _gate = new();
    private readonly Lock _writeGate = new();
    private readonly LinkedList<StoredEntry> _entries = new();
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly string _path = Path.Combine(Path.GetDirectoryName(configurationFile.Path)!, "Logs", "events.jsonl");

    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _sequence;
    private long _bytes;
    private bool _needsRewrite;
    private bool _diskUnavailable;
    private DateTimeOffset _nextWrite;
    private DateTimeOffset _nextExpiry;
    private int _retentionDays;
    private bool _eventViewerUnavailable;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        Load(cancellationToken);
        return base.StartAsync(cancellationToken);
    }

    public ServiceLogPage ReadHistory(long before, int limit)
    {
        lock (_gate)
        {
            var entries = new List<ServiceLogEntry>();
            for (var node = _entries.Last; node is not null && entries.Count < limit; node = node.Previous)
            {
                if (before == 0 || node.Value.Entry.Sequence < before)
                {
                    entries.Add(node.Value.Entry);
                }
            }

            return Page([.. entries]);
        }
    }

    public (ServiceLogPage Page, Task Changed) ReadAfter(long sequence)
    {
        lock (_gate)
        {
            var node = _entries.Last;
            while (node is not null && node.Value.Entry.Sequence > sequence)
            {
                node = node.Previous;
            }

            var entries = new List<ServiceLogEntry>();
            for (node = node is null ? _entries.First : node.Next; node is not null && entries.Count < PageSize; node = node.Next)
            {
                entries.Add(node.Value.Entry);
            }

            return (Page([.. entries]), _changed.Task);
        }
    }

    public ServiceLogPage Clear()
    {
        lock (_writeGate)
        {
            ServiceLogPage page;
            var windowsEvents = new List<ActivityRecord>();

            lock (_gate)
            {
                try
                {
                    File.Delete(_path + ".tmp");
                    File.Delete(_path);
                }
                catch (DirectoryNotFoundException)
                {
                    // There is no saved history if the log directory does not exist.
                }

                // Drop the records already queued at the time of the clear.
                var pending = input.Reader.Count;
                while (pending-- > 0 && input.Reader.TryRead(out var record))
                {
                    if (record.Event.WriteToEventViewer && eventViewer is not null)
                    {
                        windowsEvents.Add(record);
                    }
                }

                input.TakeDroppedCount();
                _entries.Clear();
                _bytes = 0;
                _needsRewrite = false;
                _diskUnavailable = false;
                _nextWrite = default;
                SignalChange();
                page = Page([]);
            }

            if (OperatingSystem.IsWindows() && eventViewer is not null)
            {
                WriteWindowsEvents(eventViewer, windowsEvents);
            }

            return page;
        }
    }

    private ServiceLogPage Page(ServiceLogEntry[] entries) =>
        new(_instanceId, _entries.First?.Value.Entry.Sequence ?? _sequence + 1, _sequence, entries);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                PublishPending();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            PublishPending(flush: true);
        }
    }

    private void Load(CancellationToken token)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            if (new FileInfo(_path).Length > MonitoringOptions.MaxLogStorageMb * 1024L * 1024L)
            {
                _needsRewrite = true;
                logger.LogWarning("RTX Lens event history exceeded its maximum size and will be replaced");
                return;
            }

            foreach (var line in File.ReadLines(_path, Utf8))
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    var entry = JsonSerializer.Deserialize(line, AppJsonContext.Default.ServiceLogEntry);
                    if (entry is null || entry.Sequence <= _sequence || !Enum.IsDefined(entry.Level)
                        || string.IsNullOrWhiteSpace(entry.Code) || string.IsNullOrWhiteSpace(entry.Source)
                        || string.IsNullOrWhiteSpace(entry.Message) || line.Length > 8192)
                    {
                        _needsRewrite = true;
                        continue;
                    }

                    _sequence = entry.Sequence;
                    Add(new StoredEntry(entry, line));
                }
                catch (JsonException)
                {
                    _needsRewrite = true;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _needsRewrite = true;
            ReportDiskError(error);
        }
    }

    private void PublishPending(bool flush = false)
    {
        lock (_writeGate)
        {
            PublishPendingCore(flush);
        }
    }

    private void PublishPendingCore(bool flush)
    {
        var additions = new List<StoredEntry>();
        var windowsEvents = new List<ActivityRecord>();
        var sequence = _sequence;
        while (additions.Count < 1024 && input.Reader.TryRead(out var record))
        {
            additions.Add(Serialize(record.Entry with { Sequence = ++sequence }));
            if (record.Event.WriteToEventViewer && eventViewer is not null)
            {
                windowsEvents.Add(record);
            }
        }

        var now = clock.GetUtcNow();
        var dropped = input.TakeDroppedCount();
        if (dropped > 0)
        {
            additions.Add(Serialize(new ServiceLogEntry(++sequence, now, ServiceLogLevel.Warning,
                "logs.overloaded", "Service", "Some events could not be recorded because the log queue was full.",
                $"Skipped events: {dropped}.")));
        }

        var current = configuration.Current;
        var settings = current.Configuration.Monitoring;
        var removed = false;

        lock (_gate)
        {
            _sequence = sequence;
            foreach (var entry in additions)
            {
                Add(entry);
            }

            if (current.Revision > 0 && (_retentionDays != settings.EventLogRetentionDays || now >= _nextExpiry))
            {
                _retentionDays = settings.EventLogRetentionDays;
                _nextExpiry = now.AddMinutes(1);
                var cutoff = now.AddDays(-_retentionDays);

                for (var node = _entries.First; node is not null;)
                {
                    var next = node.Next;
                    if (node.Value.Entry.Timestamp < cutoff)
                    {
                        Remove(node);
                        removed = true;
                    }

                    node = next;
                }
            }

            var capacity = settings.EventLogStorageMb * 1024L * 1024L;
            while (current.Revision > 0 && _bytes > capacity && _entries.First is { } first)
            {
                Remove(first);
                removed = true;
            }

            if (additions.Count > 0 || removed)
            {
                SignalChange();
            }
        }

        if (OperatingSystem.IsWindows() && eventViewer is not null)
        {
            WriteWindowsEvents(eventViewer, windowsEvents);
        }

        _needsRewrite |= removed;
        if (!flush && now < _nextWrite)
        {
            _needsRewrite |= additions.Count > 0;
            return;
        }

        if (_needsRewrite || additions.Count > 0)
        {
            Persist(additions, now);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void WriteWindowsEvents(ActivityEventLogProvider provider, List<ActivityRecord> records)
    {
        foreach (var record in records)
        {
            try
            {
                provider.Write(record);
                _eventViewerUnavailable = false;
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception
                or UnauthorizedAccessException or System.Security.SecurityException)
            {
                if (!_eventViewerUnavailable)
                {
                    _eventViewerUnavailable = true;
                    logger.LogActivity(LogLevel.Warning, "logs.windows-unavailable", "Service", "Could not write to Windows Event Viewer.");
                }
            }
        }
    }

    private void SignalChange()
    {
        var changed = _changed;
        _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        changed.TrySetResult();
    }

    private void Add(StoredEntry entry)
    {
        _entries.AddLast(entry);
        _bytes += entry.Bytes;
    }

    private void Remove(LinkedListNode<StoredEntry> node)
    {
        _bytes -= node.Value.Bytes;
        _entries.Remove(node);
    }

    private void Persist(List<StoredEntry> additions, DateTimeOffset now)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            if (_needsRewrite)
            {
                var temporary = _path + ".tmp";
                File.WriteAllLines(temporary, _entries.Select(entry => entry.Line), Utf8);
                File.Move(temporary, _path, overwrite: true);
                _needsRewrite = false;
            }
            else
            {
                File.AppendAllLines(_path, additions.Select(entry => entry.Line), Utf8);
            }

            _diskUnavailable = false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _needsRewrite = true;
            _nextWrite = now.AddSeconds(30);
            ReportDiskError(error);
        }
    }

    private void ReportDiskError(Exception error)
    {
        if (!_diskUnavailable)
        {
            _diskUnavailable = true;
            logger.LogWarning(error, "Could not access RTX Lens event history on disk. Recent activity is still available in memory");
            logger.LogActivity(LogLevel.Warning, "logs.storage-unavailable", "Service",
                "Event history could not be saved. Recent activity is available in memory.");
        }
    }

    private static StoredEntry Serialize(ServiceLogEntry entry) =>
        new(entry, JsonSerializer.Serialize(entry, AppJsonContext.Default.ServiceLogEntry));

    private sealed record StoredEntry(ServiceLogEntry Entry, string Line)
    {
        public int Bytes { get; } = Utf8.GetByteCount(Line) + Environment.NewLine.Length;
    }
}
