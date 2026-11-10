using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using RTXLens.App.Debugging.Windows;
using RTXLens.Debugging;
using RTXLens.Debugging.Api;
using RTXLens.Debugging.Models;
using RTXLens.Presentation.Configuration;
using RTXLens.Presentation.Notifications;
using RTXLens.Presentation.Telemetry;

namespace RTXLens.App.Services;

internal sealed partial class AppRuntime
{
    private DebugMonitoringApi _debugApi = null!;
    private ConfigurationEditorViewModel _debugConfiguration = null!;
    private DashboardViewModel _debugDashboard = new();
    private WarningTransitionTracker _debugWarnings = new();
    private DispatcherQueueTimer _debugTimer = null!;
    private DebugFrame? _debugFrame;
    private long _debugRevision = -1;
    private bool _wasDebugEnabled;
    private bool _publishingDebug;
    private RTXLens.Plugins.WireView.Contracts.Common.DeviceConfig? _liveWireViewConfiguration;
    public DebugSession Debug { get; } = new();
    internal DebugWindow? DebugConsole { get; private set; }
    internal int DebugNotificationCount { get; private set; }

    private void InitializeDebugging()
    {
        _debugApi = new DebugMonitoringApi(Debug, TimeProvider.System);
        _debugConfiguration = new ConfigurationEditorViewModel(() => _debugApi, localOnly: true);
        _debugTimer = _dispatcher.CreateTimer();
        _debugTimer.Interval = TimeSpan.FromSeconds(1);
        _debugTimer.Tick += (_, _) => PublishDebugFrame();
        Debug.Changed += OnDebugChanged;
    }

    internal void OpenDebugConsole()
    {
        if (DebugConsole is not null)
        {
            DebugConsole.AppWindow.Show();
            DebugConsole.Activate();
            return;
        }
        DebugConsole = new DebugWindow(this);
        DebugConsole.Closed += (_, _) => DebugConsole = null;
        TrackWindow(DebugConsole, "debug-console", 640, 850, besideOwner: true);
        DebugConsole.Activate();
    }

    private void OnDebugChanged(bool structural)
    {
        var transition = _wasDebugEnabled != Debug.Enabled;
        _wasDebugEnabled = Debug.Enabled;
        if (transition)
        {
            Performance.SwitchMode(Debug.Enabled ? "simulation" : "live");
            if (!Debug.Enabled && _performanceLiveConfiguration is { } configuration)
            {
                Performance.SetConfiguration(configuration);
            }
        }

        if (transition && Debug.Enabled)
        {
            _debugDashboard = new DashboardViewModel();
            _debugConfiguration = new ConfigurationEditorViewModel(() => _debugApi, localOnly: true);
            _debugWarnings = new WarningTransitionTracker();
            _debugRevision = -1;
            DebugNotificationCount = 0;
            _debugTimer.Start();
        }
        if (Debug.Enabled)
        {
            PublishDebugFrame();
        }
        else if (transition)
        {
            _debugTimer.Stop();
            _debugFrame = null;
        }
        if (transition)
        {
            _refresh = true;
            _rendered = null;
            _window?.RefreshRuntime(Debug.Enabled);
            RenderLatest();
            if (!Debug.Enabled && _session?.Latest is { } live && Preferences.WarningNotifications)
            {
                foreach (var warning in _warnings.Evaluate(live, DateTimeOffset.UtcNow, Preferences))
                {
                    ShowNotification(warning.Title, warning.Message);
                }
            }
        }
        else if (Debug.Enabled && _rendering)
        {
            RenderLatest();
        }
    }

    private void PublishDebugFrame()
    {
        if (!Debug.Enabled || _publishingDebug || StopToken.IsCancellationRequested)
        {
            return;
        }

        _publishingDebug = true;
        try
        {
            var now = DateTimeOffset.UtcNow;
            _debugFrame = Debug.Compose(now);
            Performance.SetCatalog(_debugFrame.Catalog);
            Performance.SetConfiguration(_debugFrame.Configuration.Configuration);
            Performance.Accept(_debugFrame.Snapshot);
            _debugDashboard.SetCatalog(_debugFrame.Catalog);
            if (_debugRevision != _debugFrame.Configuration.Revision)
            {
                _debugRevision = _debugFrame.Configuration.Revision;
                _debugDashboard.SetConfiguration(_debugFrame.Configuration);
                _debugConfiguration.SetCatalog(_debugFrame.Catalog);
                _debugConfiguration.Receive(_debugFrame.Configuration);
            }
            _debugDashboard.Connection = "DEBUG SIMULATION ACTIVE · " + _liveDashboard.Connection;
            foreach (var warning in _debugWarnings.Evaluate(_debugFrame.Snapshot, now, Preferences))
            {
                if (!Preferences.WarningNotifications)
                {
                    continue;
                }

                DebugNotificationCount++;
                Debug.ReportNotification(warning.Title, warning.Message);
                ShowNotification("DEBUG · " + warning.Title, warning.Message);
            }
        }
        finally { _publishingDebug = false; }
    }

    private void StopDebugging()
    {
        _debugTimer.Stop();
        Debug.Changed -= OnDebugChanged;
        DebugConsole?.Close();
    }

    private void ApplyDebugDeviceConfiguration()
    {
        if (!Debug.Enabled || _debugFrame is null)
        {
            return;
        }

        var wire = _debugDashboard.Panels.FirstOrDefault(panel => panel.IsWireView);
        if (wire is not { IsReporting: true, DeviceDetails.Count: 0 })
        {
            return;
        }

        var configuration = IsWireViewDeviceSimulated ? Debug.GetSimulatedWireViewConfiguration() : _liveWireViewConfiguration;
        if (configuration is null)
        {
            return;
        }

        wire.SetDeviceConfiguration(configuration, IsWireViewDeviceSimulated ? "Simulated WireView settings loaded." : null);
        wire.Apply(_debugFrame.Snapshot, Preferences, DateTimeOffset.UtcNow);
    }
}
