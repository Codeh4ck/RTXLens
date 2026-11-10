using Microsoft.Extensions.Logging;

namespace RTXLens.Core.Diagnostics;

public sealed record ActivityEvent(string Code, string Source, string Message, string? Detail, bool WriteToEventViewer)
{
    public override string ToString() => Detail is null ? Message : $"{Message} {Detail}";
}

public static class ActivityLogging
{
    public static void LogActivity(this ILogger logger, LogLevel level, string code, string source, string message,
        string? detail = null, bool eventViewer = false, Exception? exception = null)
    {
        if (!logger.IsEnabled(level))
        {
            return;
        }

        var activity = new ActivityEvent(code, source, message, detail, eventViewer);
        logger.Log(level, new EventId(0, code), activity, exception, static (value, _) => value.ToString());
    }
}
