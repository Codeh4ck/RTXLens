using System.Threading.Channels;
using Microsoft.Extensions.Logging.EventLog;
using RTXLens.Contracts.Diagnostics;
using RTXLens.Core.Diagnostics;

namespace RTXLens.Service.Diagnostics;

internal sealed record ActivityRecord(ServiceLogEntry Entry, LogLevel Level, ActivityEvent Event, Exception? Exception);

internal sealed class ActivityLoggerProvider(TimeProvider clock) : ILoggerProvider
{
    private readonly Channel<ActivityRecord> _queue = Channel.CreateBounded<ActivityRecord>(
        new BoundedChannelOptions(1024) { SingleReader = true, AllowSynchronousContinuations = false });

    private readonly TimeProvider _clock = clock;
    private int _dropped;
    public ChannelReader<ActivityRecord> Reader => _queue.Reader;
    public int TakeDroppedCount() => Interlocked.Exchange(ref _dropped, 0);

    public ILogger CreateLogger(string categoryName) => new ActivityLogger(this);

    public void Dispose() => _queue.Writer.TryComplete();

    private sealed class ActivityLogger(ActivityLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => level is LogLevel.Information or LogLevel.Warning or LogLevel.Error or LogLevel.Critical;

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is not ActivityEvent activity || !IsEnabled(level))
            {
                return;
            }

            var severity = level switch
            {
                LogLevel.Information => ServiceLogLevel.Information,
                LogLevel.Warning => ServiceLogLevel.Warning,
                _ => ServiceLogLevel.Error
            };

            var entry = new ServiceLogEntry(0, owner._clock.GetUtcNow(), severity, activity.Code,
                Limit(activity.Source, 120), Limit(activity.Message, 500),
                activity.Detail is { } detail ? Limit(detail, 1000) : null);

            if (!owner._queue.Writer.TryWrite(new ActivityRecord(entry, level, activity, exception)))
            {
                Interlocked.Increment(ref owner._dropped);
            }
        }

        private static string Limit(string text, int maxLength) => text.Length <= maxLength ? text : text[..(maxLength - 1)] + "…";
    }
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class ActivityEventLogProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly EventLogLoggerProvider _inner;
    private readonly ILogger _activity;

    public ActivityEventLogProvider(EventLogSettings settings)
    {
        _inner = new EventLogLoggerProvider(settings);
        _activity = _inner.CreateLogger("RTXLens.BackendActivity");
    }

    public void Write(ActivityRecord record) => _activity.Log(record.Level, new EventId(0, record.Event.Code),
        record.Event, record.Exception, static (activity, _) => activity.ToString());

    public ILogger CreateLogger(string categoryName) => new EventLogger(_inner.CreateLogger(categoryName));
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _inner.SetScopeProvider(scopeProvider);
    public void Dispose() => _inner.Dispose();

    private sealed class EventLogger(ILogger inner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
        public bool IsEnabled(LogLevel level) => inner.IsEnabled(level);

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is not ActivityEvent)
            {
                inner.Log(level, eventId, state, exception, formatter);
            }
        }
    }
}
