using RTXLens.Core.Configuration;
using RTXLens.Core.Diagnostics;
using RTXLens.Core.Plugins;

namespace RTXLens.Service.Services;

internal sealed class HardwareDiscoveryService(PluginRuntimeManager runtime, IRuntimeConfiguration configuration, ILogger<HardwareDiscoveryService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(runtime.Factories.Select(factory => WatchAsync(factory, stoppingToken)));

    private async Task WatchAsync(ISensorPluginFactory factory, CancellationToken stoppingToken)
    {
        var presence = new HardwarePresenceTracker();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var settings = configuration.Current.Configuration.Plugins[factory.Id];
            if (settings is { Enabled: false, AutomaticallyDisabled: false })
            {
                presence.Observe(settings, null);
                continue;
            }

            try
            {
                var detected = presence.Observe(settings, factory.HardwarePresent(settings));
                if (detected is null)
                {
                    continue;
                }

                await configuration.UpdateAsync(candidate =>
                {
                    // Another device can change independently while this probe is in flight.
                    if (!candidate.Plugins[factory.Id].HasSameValues(settings))
                    {
                        throw new ConfigurationConflictException();
                    }

                    candidate.Plugins[factory.Id] = detected;
                    return candidate;
                }, stoppingToken);

                var message = detected.Enabled
                    ? "Matching hardware was found. Plugin enabled automatically."
                    : "No matching hardware was found. Plugin disabled automatically.";

                logger.LogActivity(LogLevel.Information, "plugin.auto-detection", factory.Name, message);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                presence.Observe(settings, null);
                logger.LogDebug(exception, "Hardware detection inconclusive for {PluginId}; retaining settings", factory.Id);
            }
        }
    }
}
