namespace RTXLens.Contracts.Diagnostics;

public enum ServiceLogLevel
{
    Information,
    Warning,
    Error
}

public sealed record ServiceLogEntry(long Sequence, DateTimeOffset Timestamp, ServiceLogLevel Level,
    string Code, string Source, string Message, string? Detail);

public sealed record ServiceLogPage(string InstanceId, long FirstSequence, long LatestSequence, ServiceLogEntry[] Entries);
