using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using RTXLens.Contracts.Configuration;
using RTXLens.Core.Diagnostics;
using RTXLens.Service.Configuration;

namespace RTXLens.Service.Services;

internal interface IWarningShutdown
{
    void Request();
}

internal sealed class WindowsWarningShutdown : IWarningShutdown
{
    public void Request()
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
        if (!process.WaitForExit(5000) || process.ExitCode != 0)
        {
            throw new InvalidOperationException("Windows did not confirm the shutdown command.");
        }
    }
}

internal sealed class WarningShutdownCoordinator(RuntimeConfigurationFile configurationFile, TimeProvider clock, IWarningShutdown shutdown, ILogger<WarningShutdownCoordinator> logger)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Countdown> _countdowns = new(StringComparer.Ordinal);
    private readonly DateTimeOffset _boot = clock.GetUtcNow() - TimeSpan.FromMilliseconds(Environment.TickCount64);
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);
    private bool _requested;

    private string ReasonPath => Path.Combine(Path.GetDirectoryName(configurationFile.Path)!, "last-warning-shutdown.json");

    public void Evaluate(IReadOnlyList<WarningShutdownCandidate> candidates)
    {
        if (_requested)
        {
            return;
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

            if (_failed.Contains(warning.Key) || warning.ObservedAt - timer.Start < TimeSpan.FromSeconds(warning.Profile.ShutdownAfterSeconds!.Value))
            {
                continue;
            }

            if (Request(warning))
            {
                _requested = true;
                return;
            }

            _failed.Add(warning.Key);
        }

        foreach (var key in _countdowns.Keys.Where(key => !remaining.Contains(key)).ToArray())
        {
            var countdown = _countdowns[key];
            _countdowns.Remove(key);

            if (!_failed.Remove(key))
            {
                logger.LogActivity(LogLevel.Information, "protection.shutdown-cancelled", DeviceWarningEvaluator.SourceLabel(countdown.Profile),
                    "Shutdown countdown cancelled because the warning is no longer confirmed.");
            }
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

    private bool Request(WarningShutdownCandidate warning)
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

                shutdown.Request();

                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
            {
                logger.LogActivity(LogLevel.Error, "protection.shutdown-failed", reason.Source,
                    "The service could not request Windows shutdown.", reason.Warning, eventViewer: true, exception: error);

                if (recorded)
                {
                    try
                    {
                        File.Delete(ReasonPath);
                    }
                    catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
                    {
                        logger.LogWarning(cleanupError, "Could not remove the failed warning shutdown reason");
                    }
                }

                return false;
            }
        }
    }

    private sealed record Countdown(DeviceWarningProfile Profile, ConnectorWarningLimits Limits, DateTimeOffset Start, DateTimeOffset Last);
}
