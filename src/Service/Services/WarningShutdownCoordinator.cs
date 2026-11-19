using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using RTXLens.Contracts.Configuration;
using RTXLens.Core.Diagnostics;
using RTXLens.Service.Configuration;

namespace RTXLens.Service.Services;

internal enum ShutdownOutcome { Accepted, Rejected, Unconfirmed, RetryableFailure }

internal interface IWarningShutdown
{
    ShutdownOutcome Request();
}

internal sealed class WindowsWarningShutdown : IWarningShutdown
{
    public ShutdownOutcome Request()
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in new[] { "/s", "/t", "0", "/d", "p:0:0", "/c", "RTX Lens protection warning" })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Windows shutdown command.");
        try
        {
            if (!process.WaitForExit(5000))
            {
                return ShutdownOutcome.Unconfirmed;
            }

            return process.ExitCode == 0 ? ShutdownOutcome.Accepted : ShutdownOutcome.Rejected;
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        {
            // The process already started. Its outcome cannot safely be retried.
            return ShutdownOutcome.Unconfirmed;
        }
    }
}

internal sealed class WarningShutdownCoordinator(RuntimeConfigurationFile configurationFile, TimeProvider clock, IWarningShutdown shutdown, ILogger<WarningShutdownCoordinator> logger)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Countdown> _countdowns = new(StringComparer.Ordinal);
    private readonly DateTimeOffset _boot = clock.GetUtcNow() - TimeSpan.FromMilliseconds(Environment.TickCount64);
    private bool _requested;
    private bool _rejected;
    private DateTimeOffset _retryAfter;
    private int _retryDelaySeconds;

    private string ReasonPath => Path.Combine(Path.GetDirectoryName(configurationFile.Path)!, "last-warning-shutdown.json");

    public void Evaluate(IReadOnlyList<WarningShutdownCandidate> candidates)
    {
        if (_requested)
        {
            return;
        }

        if (candidates.Count == 0)
        {
            _rejected = false;
            _retryAfter = default;
            _retryDelaySeconds = 0;
        }

        var remaining = new HashSet<string>(StringComparer.Ordinal);
        foreach (var warning in candidates)
        {
            remaining.Add(warning.Key);
            if (!_countdowns.ContainsKey(warning.Key))
            {
                logger.LogActivity(LogLevel.Warning, "protection.shutdown-countdown", DeviceWarningEvaluator.SourceLabel(warning.Profile),
                    $"Shutdown countdown started for {warning.Definition.Title}.",
                    $"Delay: {warning.Profile.ShutdownAfterSeconds} seconds.");
            }

            if (!_countdowns.TryGetValue(warning.Key, out var timer) || timer.Profile != warning.Profile
                || timer.Limits != warning.Limits || warning.ObservedAt < timer.Last
                || warning.ObservedAt - timer.Last > warning.MaxAge)
            {
                timer = new(warning.Profile, warning.Limits, warning.ObservedAt, warning.ObservedAt);
            }

            timer = timer with { Last = warning.ObservedAt };
            _countdowns[warning.Key] = timer;

            if (_rejected || warning.ObservedAt < _retryAfter
                || warning.ObservedAt - timer.Start < TimeSpan.FromSeconds(warning.Profile.ShutdownAfterSeconds!.Value))
            {
                continue;
            }

            var outcome = Request(warning);
            if (outcome is ShutdownOutcome.Accepted or ShutdownOutcome.Unconfirmed)
            {
                _requested = true;
                return;
            }

            if (outcome == ShutdownOutcome.Rejected)
            {
                _rejected = true;
            }
            else
            {
                _retryDelaySeconds = _retryDelaySeconds == 0 ? 5 : Math.Min(60, _retryDelaySeconds * 2);
                _retryAfter = warning.ObservedAt.AddSeconds(_retryDelaySeconds);
            }
        }

        foreach (var key in _countdowns.Keys.Where(key => !remaining.Contains(key)).ToArray())
        {
            var countdown = _countdowns[key];
            _countdowns.Remove(key);

            logger.LogActivity(LogLevel.Information, "protection.shutdown-cancelled", DeviceWarningEvaluator.SourceLabel(countdown.Profile),
                "Shutdown countdown ended because the warning is no longer confirmed.");
        }
    }

    public WarningShutdownReason? ReadAfterBoot()
    {
        lock (_gate)
        {
            var reason = Read();
            return reason is not null && _boot > reason.BootTime.AddSeconds(5) ? reason : null;
        }
    }

    public void Acknowledge(string id)
    {
        lock (_gate)
        {
            if (ReadAfterBoot()?.Id == id)
            {
                File.Delete(ReasonPath);
            }
        }
    }

    private WarningShutdownReason? Read()
    {
        try
        {
            return File.Exists(ReasonPath) ? JsonSerializer.Deserialize(File.ReadAllText(ReasonPath), AppJsonContext.Default.WarningShutdownReason) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(error, "Could not read the last warning shutdown reason");
            return null;
        }
    }

    private ShutdownOutcome Request(WarningShutdownCandidate warning)
    {
        var reason = new WarningShutdownReason(Guid.NewGuid().ToString("N"), clock.GetUtcNow(), _boot,
            DeviceWarningEvaluator.SourceLabel(warning.Profile), warning.Profile.Name, warning.Definition.Title, warning.Observation.Detail);

        lock (_gate)
        {
            var recorded = false;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ReasonPath)!);
                SettingsProfileStore.WriteAtomically(ReasonPath, JsonSerializer.Serialize(reason, AppJsonContext.Default.WarningShutdownReason));

                recorded = true;

                logger.LogActivity(LogLevel.Warning, "protection.shutdown-requested", reason.Source,
                    $"Requesting Windows shutdown for {reason.Warning}.", reason.ProfileName, eventViewer: true);

                var outcome = shutdown.Request();
                if (outcome == ShutdownOutcome.Unconfirmed)
                {
                    logger.LogActivity(LogLevel.Error, "protection.shutdown-unconfirmed", reason.Source,
                        "Windows did not confirm the shutdown outcome. Further requests are suspended.", reason.Warning, eventViewer: true);
                }
                else if (outcome == ShutdownOutcome.Rejected)
                {
                    logger.LogActivity(LogLevel.Error, "protection.shutdown-rejected", reason.Source,
                        "Windows rejected the shutdown command.", reason.Warning, eventViewer: true);
                    RemoveFailedReason();
                }

                return outcome;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
            {
                logger.LogActivity(LogLevel.Error, "protection.shutdown-failed", reason.Source,
                    "The service could not request Windows shutdown.", reason.Warning, eventViewer: true, exception: error);

                if (recorded)
                {
                    RemoveFailedReason();
                }

                return ShutdownOutcome.RetryableFailure;
            }
        }
    }

    private void RemoveFailedReason()
    {
        try
        {
            File.Delete(ReasonPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(error, "Could not remove the failed warning shutdown reason");
        }
    }

    private sealed record Countdown(DeviceWarningProfile Profile, ConnectorWarningLimits Limits, DateTimeOffset Start, DateTimeOffset Last);
}
