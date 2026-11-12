#if TELEMETRY_DEBUG
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using RTXLens.App.Platform;
using RTXLens.App.Views.Simulation;
using RTXLens.Client.Http;
using RTXLens.Contracts.Sensors;
using RTXLens.Debugging;
using RTXLens.Debugging.Api;
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
    private bool _publishingSimulation;

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

        var window = new DebugConsoleWindow(Simulation);
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
            }
            else
            {
                _simulationTimer.Stop();
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
            foreach (var warning in _simulationWarnings.Evaluate(frame.Snapshot, now, Preferences))
            {
                Simulation.ReportNotification(warning.Title, warning.Message);
                WarningRaised?.Invoke(warning with { Title = "DEBUG · " + warning.Title });
            }
        }
        finally
        {
            _publishingSimulation = false;
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
