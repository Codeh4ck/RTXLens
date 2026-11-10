using RTXLens.Core.Configuration;
using RTXLens.Core.Diagnostics;
using RTXLens.Core.Plugins;

namespace RTXLens.Service.Services;

internal sealed class HardwareDiscoveryService(PluginRuntimeManager runtime, IRuntimeConfiguration configuration, ILogger<HardwareDiscoveryService> logger) : BackgroundService
{
    private readonly HardwarePresenceTracker _presence = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let normal acquisition initialize first. Detection never opens a second device transport.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach (var factory in runtime.Factories)
                {
                    var baseline = configuration.Current;
                    if (!baseline.Configuration.Plugins.TryGetValue(factory.Id, out var settings) || settings is { Enabled: false, AutomaticallyDisabled: false })
                    {
                        continue;
                    }

                    try
                    {
                        var detected = _presence.Observe(factory.Id, settings, factory.HardwarePresent(settings));
                        if (detected is not null)
                        {
                            await configuration.UpdateAsync(candidate =>
                            {
                                candidate.Plugins[factory.Id] = detected;
                                return candidate;
                            }, stoppingToken, baseline.Version);

                            var message = detected.Enabled
                                ? "Matching hardware was found. Plugin enabled automatically."
                                : "No matching hardware was found. Plugin disabled automatically.";

                            logger.LogActivity(LogLevel.Information, "plugin.auto-detection", factory.Name, message);
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        _presence.Observe(factory.Id, settings, null);
                        logger.LogDebug(exception, "Hardware detection inconclusive for {PluginId}; retaining settings", factory.Id);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }
}
