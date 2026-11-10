using System.Text.Json;
using RTXLens.Contracts.Configuration;
using RTXLens.Core;
using RTXLens.Core.Configuration;
using RTXLens.Core.Streaming;
using RTXLens.Core.Utilities;
using RTXLens.Warnings;
using RTXLens.Service.Configuration;
using RTXLens.Service.Diagnostics;

namespace RTXLens.Service.Services;

internal sealed class SnapshotBroadcastService(IRuntimeConfiguration configuration, TimeProvider clock,
    SensorStateStore store, SensorStreamHub hub, RTXLens.Core.Plugins.PluginRuntimeManager runtime,
    SettingsProfileResolver profiles, DeviceWarningProfileStore warningProfiles, WarningShutdownCoordinator shutdown,
    SafeguardJournalService safeguard, MonitoringActivity activity) : BackgroundService
{
    private readonly DeviceWarningEvaluator _profileWarnings = new();
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
                var snapshot = store.Snapshot(now, settings.Selections) with { ConfigurationRevision = current.Revision };

                snapshot = activity.ResolveConnectorSources(snapshot, current.Configuration);
                snapshot = snapshot with { SettingsProfile = profiles.Resolve(snapshot, current.Configuration) };

                safeguard.Observe(snapshot);

                warningProfiles.Evaluate(snapshot, activeProfiles =>
                {
                    var evaluated = _profileWarnings.Evaluate(snapshot, current.Configuration, activeProfiles, profiles.Automatic);

                    var warnings = _warnings.Evaluate(evaluated.Snapshot, current.Configuration, now, evaluated.Warnings,
                        observation => observation.PluginId != "asus-astral" && observation.TypeId != ActiveSettingsProfile.WireViewProfileOvercurrent);

                    snapshot = evaluated.Snapshot with { Warnings = warnings };

                    shutdown.Evaluate(evaluated.Shutdowns);
                });

                activity.Observe(snapshot, settings.ConnectorProfiles);

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
