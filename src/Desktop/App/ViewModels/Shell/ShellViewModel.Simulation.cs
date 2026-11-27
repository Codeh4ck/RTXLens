#if TELEMETRY_DEBUG
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using RTXLens.App.Platform;
using RTXLens.App.Views.Simulation;
using RTXLens.Client.Mobile;
using RTXLens.Contracts.Mobile;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Warnings;
using RTXLens.Debugging.Models;
using RTXLens.Client.Http;
using RTXLens.Contracts.Monitoring;
using RTXLens.Contracts.Sensors;
using RTXLens.Debugging;
using RTXLens.Debugging.Api;
using RTXLens.Plugins.Msi.Contracts;
using RTXLens.Plugins.WireView.Contracts;
using RTXLens.Presentation.Configuration;
using RTXLens.Presentation.Notifications;

namespace RTXLens.App.ViewModels.Shell;

internal sealed partial class ShellViewModel
{
    private readonly DispatcherTimer _simulationTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DebugSession? _simulation;
    private DebugMonitoringApi? _simulationApi;
    private DebugConsoleWindow? _debugConsole;
    private SensorSnapshot? _simulatedSnapshot;
    private WarningTransitionTracker _simulationWarnings = new();
    private ConfigurationEditorViewModel? _simulationConfiguration;
    private long _simulationRevision;
    private long _warningProfileRequest;
    private bool _publishingSimulation;
    private bool _sendingPowerFrame;
    private bool _sentPowerInputs;
    private (MonitoringApiClient Api, DebugPowerFrame Frame)? _pendingPowerFrame;

    [ObservableProperty]
    public partial bool DebugConsoleRevealed { get; private set; } = DesktopBuild.IsDebug;

    public DebugSession Simulation => _simulation ??= CreateSimulation();

    public void RevealDebugConsole() => DebugConsoleRevealed = true;

    private DebugSession CreateSimulation()
    {
        var session = new DebugSession();
        session.ReceiveSnapshot(_session.Latest);

        if (_liveCatalog is { } catalog)
        {
            session.ReceiveCatalog(catalog);
        }

        if (_liveConfiguration is { } configuration)
        {
            session.ReceiveConfiguration(configuration);
        }

        _simulationTimer.Tick += (_, _) => PublishSimulation();
        session.Changed += SimulationChanged;

        return session;
    }

    public void OpenDebugConsole(Window owner)
    {
        if (!DebugConsoleRevealed)
        {
            return;
        }

        if (_debugConsole is { } open)
        {
            open.Activate();
            return;
        }

        var window = new DebugConsoleWindow(Simulation, RefreshSavedWarningProfilesAsync);
        TrackToolWindow(window, "debug-console", owner, besideOwner: true);

        _debugConsole = window;

        window.Closed += (_, _) =>
        {
            _debugConsole = null;
            Simulation.ResetAll();
        };

        window.Show(owner);
    }

    private void SimulationChanged(bool structural)
    {
        if (_disposed)
        {
            return;
        }

        if (Simulation.Enabled != IsSimulation)
        {
            Dashboard.Panels.FirstOrDefault(panel => panel.IsWireView)?.MarkDisconnected(Preferences);

            var api = Simulation.Enabled ? new DebugMonitoringApi(Simulation, TimeProvider.System) : null;

            lock (_recordingModeLock)
            {
                Volatile.Write(ref _simulationApi, api);
                Recorder.SwitchMode(api is not null ? "simulation" : "live");
            }

            _displayedSnapshot = null;
            if (api is not null)
            {
                _simulationConfiguration = new ConfigurationEditorViewModel(() => api, localOnly: true);
                _simulationWarnings = new WarningTransitionTracker();
                _simulationRevision = -1;
                Dashboard.Connection = "DEBUG SIMULATION ACTIVE";

                _simulationTimer.Start();
                PublishSimulation();
                _ = RefreshSavedWarningProfilesAsync();
            }
            else
            {
                _simulationTimer.Stop();
                QueuePowerFrame(new([], []));

                _simulatedSnapshot = null;
                _simulationConfiguration = null;
                Dashboard.Connection = _liveConnection;

                if (_liveCatalog is { } catalog)
                {
                    Recorder.SetCatalog(catalog);
                    Dashboard.SetCatalog(catalog);
                    Configuration.SetCatalog(catalog);
                }
                else
                {
                    Dashboard.SetCatalog(new Contracts.Discovery.PluginCatalog(1, []));
                }

                if (_liveConfiguration is { } configuration)
                {
                    Recorder.SetConfiguration(configuration.Configuration);
                    Dashboard.SetConfiguration(configuration);
                    Configuration.Receive(configuration);
                }

                if (!_connected)
                {
                    Dashboard.Disconnect(Preferences);
                }

                RefreshPresentation(force: true);
            }

            RaiseApiChanged();
            OnPropertyChanged(nameof(Configuration));

            if (ShowConfiguration)
            {
                _ = RefreshDeviceListsAsync();
            }
        }
        else if (Simulation.Enabled)
        {
            PublishSimulation();

            if (structural)
            {
                OnPropertyChanged(nameof(CanOpenWireViewSettings));
                OnPropertyChanged(nameof(SimulatedWarningSources));
            }
        }
    }

    private async Task RefreshSavedWarningProfilesAsync()
    {
        if (_simulationApi is not { } simulationApi)
        {
            return;
        }

        var api = _liveApi;
        Simulation.ReportWarningProfilesStatus("Loading saved warning profiles…");
        var request = _warningProfileRequest + 1;
        try
        {
            await LoadWarningProfilesAsync(api);
        }
        catch (Exception error) when (error is HttpRequestException or MonitoringApiException or IOException
            or System.Text.Json.JsonException or OperationCanceledException or ObjectDisposedException)
        {
            if (ReferenceEquals(simulationApi, _simulationApi) && ReferenceEquals(api, _liveApi)
                && request == _warningProfileRequest && !StopToken.IsCancellationRequested)
            {
                Simulation.ReportWarningProfilesStatus("Could not load saved warning profiles. Check the service connection and reload.");
            }
        }
    }

    private void PublishSimulation()
    {
        if (!IsSimulation || _disposed || _publishingSimulation)
        {
            return;
        }

        _publishingSimulation = true;
        try
        {
            var now = DateTimeOffset.UtcNow;
            Simulation.ReceiveSnapshot(_session.Latest);

            var frame = Simulation.Compose(now);
            _simulatedSnapshot = frame.Snapshot;
            PublishPowerInputs(frame.Snapshot);

            if (_simulationRevision != frame.Configuration.Revision)
            {
                _simulationRevision = frame.Configuration.Revision;

                Recorder.SetCatalog(frame.Catalog);
                Recorder.SetConfiguration(frame.Configuration.Configuration);
                Dashboard.SetCatalog(frame.Catalog);
                Dashboard.SetConfiguration(frame.Configuration);
                Configuration.SetCatalog(frame.Catalog);
                Configuration.Receive(frame.Configuration);
            }

            Recorder.Accept(frame.Snapshot);
            RefreshPresentation(force: true);

            foreach (var warning in _simulationWarnings.Evaluate(frame.Snapshot.Warnings, Preferences.WarningNotifications || Preferences.PlayWarningSound))
            {
                Simulation.ReportNotification(warning.Title, warning.Message);
                WarningRaised?.Invoke(warning with { Title = "DEBUG · " + warning.Title });

                if (DesktopBuild.IsDebug && OperatingSystem.IsWindows()
                                         && Uri.TryCreate(Preferences.ServiceAddress, UriKind.Absolute, out var address)
                                         && address is { IsLoopback: true, Port: ServiceEndpoint.DebugPort })
                {
                    _ = PublishMobileSimulationWarningAsync(warning);
                }
            }
        }
        finally
        {
            _publishingSimulation = false;
        }
    }

    private void PublishPowerInputs(SensorSnapshot snapshot)
    {
        var pins = new List<SensorReading>();
        var flags = new List<DebugPowerFlag>();
        foreach (var plugin in Simulation.Plugins.Where(p => p.Mode != DebugSourceMode.Live &&
            snapshot.Plugins.Any(health => health.PluginId == p.Id && health.State == PluginState.Ready)))
        {
            var edits = plugin.Metrics.Where(m => m.Mode != DebugMetricMode.Live).Select(m => m.Definition).ToArray();
            var connectors = edits.Where(m => m.Component is SensorComponent.GpuPin or SensorComponent.PsuPin
                && m.Metric is SensorMetric.Current or SensorMetric.Voltage).Select(m => m.ConnectorId).ToHashSet();
            pins.AddRange(snapshot.Sensors.Where(r => r.Identity.PluginId == plugin.Id && r.Identity.Channel is >= 1 and <= 6
                && r.Identity.Component is SensorComponent.GpuPin or SensorComponent.PsuPin
                && r.Identity.Metric is SensorMetric.Current or SensorMetric.Voltage
                && (plugin.IsSimulated || connectors.Contains(r.Identity.ConnectorId))));
            if (plugin.IsSimulated || edits.Any(m => m.Metric is SensorMetric.OvercurrentDetected or SensorMetric.ImbalanceDetected or SensorMetric.FaultStatus))
            {
                flags.AddRange((snapshot.Warnings?.States ?? []).Where(w => w.PluginId == plugin.Id && w.Phase == WarningPhase.Active)
                    .Select(w => new DebugPowerFlag(w.PluginId, w.DeviceId, w.TypeId)));
            }
        }
        var simulated = Simulation.Plugins.Where(plugin => plugin.Mode != DebugSourceMode.Live).ToDictionary(plugin => plugin.Id);
        var warnings = (snapshot.Warnings?.States ?? []).Where(warning => simulated.TryGetValue(warning.PluginId, out var plugin)
            && (plugin.IsSimulated || plugin.Metrics.Any(metric => metric.Mode != DebugMetricMode.Live
                && (warning.ConnectorId is null || metric.Definition.ConnectorId == warning.ConnectorId)))).Take(224).ToArray();
        var devices = warnings.Select(warning => (warning.PluginId, warning.DeviceId)).ToHashSet();
        var includeGpuContext = warnings.Any(warning => warning.PluginId is WireViewIdentity.Id or MsiIdentity.Id);
        var valid = snapshot.Sensors.Where(reading => reading is { Quality: ReadingQuality.Valid, Value: { } value } && double.IsFinite(value));
        var historyReadings = valid.Where(reading => devices.Contains((reading.Identity.PluginId, reading.Identity.DeviceId)))
            .Concat(includeGpuContext
                ? valid.Where(reading => (reading.Identity.IsGpuStatus() || reading.Identity.IsGpuDiagnostic())
                    && !devices.Contains((reading.Identity.PluginId, reading.Identity.DeviceId)))
                : [])
            .Take(512).ToArray();
        QueuePowerFrame(new([.. pins], [.. flags]) { History = new(warnings, historyReadings) });
    }

    private void QueuePowerFrame(DebugPowerFrame frame)
    {
        if (!_connected || !_liveApi.Address.IsLoopback)
        {
            return;
        }

        var hasInputs = frame.Pins.Length != 0 || frame.Flags.Length != 0 || frame.History?.Warnings.Length > 0;
        if (!hasInputs && !_sentPowerInputs)
        {
            return;
        }

        _sentPowerInputs = hasInputs;
        _pendingPowerFrame = (_liveApi, frame with { History = frame.History ?? new([], []) });
        if (!_sendingPowerFrame)
        {
            _ = SendPowerFramesAsync();
        }
    }

    private async Task SendPowerFramesAsync()
    {
        _sendingPowerFrame = true;
        try
        {
            // Serialize on the dispatcher and coalesce edits while a request is in flight.
            while (_pendingPowerFrame is { } pending && !StopToken.IsCancellationRequested)
            {
                _pendingPowerFrame = null;
                try
                {
                    await pending.Api.PublishDebugPowerFrameAsync(pending.Frame, StopToken);
                }
                catch (Exception error) when (error is HttpRequestException or MonitoringApiException or IOException or OperationCanceledException or ObjectDisposedException)
                {
                    if (!StopToken.IsCancellationRequested)
                    {
                        Simulation.ReportNotification("Debug power reaction unavailable", error.Message);
                    }
                }
            }
        }
        finally { _sendingPowerFrame = false; }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private async Task PublishMobileSimulationWarningAsync(WarningNotification warning)
    {
        var message = new MobileSimulationWarning(warning.Title[..Math.Min(warning.Title.Length, 160)],
            warning.Message[..Math.Min(warning.Message.Length, 480)]);
        try
        {
            var response = await new MobileAccessClient(debug: true).SendAsync(
                new MobileAccessRequest(MobileAccessOperation.PublishSimulationWarning, SimulationWarning: message), StopToken);

            if (!response.Success)
            {
                System.Diagnostics.Debug.WriteLine($"Mobile simulation warning could not be sent: {response.Error}");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException
            or System.Security.SecurityException or System.Text.Json.JsonException)
        {
            System.Diagnostics.Debug.WriteLine($"Mobile simulation warning could not be sent: {error.Message}");
        }
    }

    private void StopSimulation()
    {
        _simulationTimer.Stop();
        if (_simulation is { } session)
        {
            session.Changed -= SimulationChanged;
        }

        _debugConsole?.Close();
    }
}
#endif
