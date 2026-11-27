using RTXLens.Core.Plugins;
using RTXLens.Contracts.Monitoring;
using System.Diagnostics;
using System.Text.Json;
using RTXLens.Contracts.Diagnostics;
using RTXLens.Contracts.Configuration;
using RTXLens.Core;
using RTXLens.Core.Configuration;
using RTXLens.Core.Streaming;
using RTXLens.Core.Utilities;
using RTXLens.Warnings;
using RTXLens.Service.Configuration;
using RTXLens.Service.Diagnostics;
using RTXLens.Service.Mobile;

namespace RTXLens.Service.Services;

internal sealed class SnapshotBroadcastService(
    IRuntimeConfiguration configuration,
    TimeProvider clock,
    SensorStateStore store,
    SensorStreamHub hub,
    [FromKeyedServices(ServiceRoutes.WarningStream)] SensorStreamHub warningHub,
    PluginRuntimeManager runtime,
    HardwareCatalog hardware,
    ProfileGpuDiscovery gpuDiscovery,
    SettingsProfileResolver profiles,
    DeviceWarningProfileStore warningProfiles,
    WarningShutdownCoordinator shutdown,
    SafeguardJournalService safeguard,
    MonitoringActivity activity,
    MobileAccessService? mobileAccess = null,
    WarningPowerService? power = null,
    DebugPowerSimulation? simulation = null) : BackgroundService
{
    private readonly DeviceWarningEvaluator _profileWarnings = new(hardware.Find);
    private readonly AsyncSignal _wake = new();
    private readonly WarningEngine _warnings = new(runtime.WarningRegistry);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        configuration.Changed += _wake.Set;
        try
        {
            DateTimeOffset? lastPublication = null;
            long publishedWarnings = -1;
            long publishedConfiguration = -1;
            ActiveSettingsProfile? publishedProfile = null;

            while (!stoppingToken.IsCancellationRequested)
            {
                var current = configuration.Current;
                var settings = current.Configuration.Monitoring;
                var now = clock.GetUtcNow();
                var started = Stopwatch.GetTimestamp();
                var snapshot = store.Snapshot(now, settings.Selections) with { ConfigurationRevision = current.Revision };

                snapshot = activity.ResolveConnectorSources(snapshot, current.Configuration);
                snapshot = snapshot with { SettingsProfile = profiles.Resolve(snapshot, current.Configuration, snapshot.Sensors.Length == 0 ? [] : gpuDiscovery.GetCachedDevices()) };

                safeguard.Observe(snapshot);

                warningProfiles.Evaluate(snapshot, activeProfiles =>
                {
                    var evaluated = _profileWarnings.Evaluate(snapshot, current.Configuration, activeProfiles, profiles.Automatic);

                    var observations = simulation?.Include(snapshot, current.Configuration, activeProfiles, profiles.Automatic, evaluated.Warnings) ?? evaluated.Warnings;
                    var warnings = _warnings.Evaluate(evaluated.Snapshot, current.Configuration, now, observations,
                        observation => hardware.UsePluginWarning(observation.PluginId, observation.TypeId));

                    snapshot = evaluated.Snapshot with { Warnings = warnings };

                    shutdown.Evaluate(evaluated.Shutdowns);
                    power?.Observe(activeProfiles, observations, warnings);
                });

                TelemetryMetrics.SnapshotDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);

                activity.Observe(snapshot, settings.ConnectorProfiles);
                mobileAccess?.ObserveWarnings(snapshot, simulation?.GetHistory());

                if (publishedWarnings != _warnings.Revision && snapshot.Warnings is { } feed)
                {
                    warningHub.Publish(JsonSerializer.SerializeToUtf8Bytes(feed, AppJsonContext.Default.WarningSnapshot));
                }

                if (
                    lastPublication is null
                    || now - lastPublication >= TimeSpan.FromMilliseconds(settings.StreamIntervalMs)
                    || publishedWarnings != _warnings.Revision || publishedConfiguration != current.Revision
                    || publishedProfile != snapshot.SettingsProfile
                )
                {
                    snapshot = snapshot with { SafeguardExecutions = safeguard.Notices };
                    hub.Publish(JsonSerializer.SerializeToUtf8Bytes(snapshot, AppJsonContext.Default.SensorSnapshot));

                    lastPublication = now;
                    publishedWarnings = _warnings.Revision;
                    publishedConfiguration = current.Revision;
                    publishedProfile = snapshot.SettingsProfile;
                }

                await _wake.WaitAsync(Math.Min(settings.StreamIntervalMs, 250), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        finally
        {
            configuration.Changed -= _wake.Set;
        }
    }
}
