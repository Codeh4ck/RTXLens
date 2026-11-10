using Microsoft.UI.Dispatching;
using RTXLens.App.Platform;
using RTXLens.App.Platform.Startup;
using RTXLens.App.Platform.Tray;
using RTXLens.Client.Http;
using RTXLens.Client.Streaming;
using RTXLens.Plugins.WireView.Contracts.Input;
using RTXLens.Presentation.Configuration;
using RTXLens.Presentation.Notifications;
using RTXLens.Presentation.Settings;
using RTXLens.Presentation.Telemetry;
using RTXLens.Presentation.Performance;

namespace RTXLens.App.Services;

internal sealed partial class AppRuntime
{
    private readonly CancellationTokenSource _stop = new();
    private readonly PreferencesStore _store;
    private AppPreferences _lastSavedPreferences;
    private readonly SemaphoreSlim _preferenceSaveGate = new(1);
    internal DesktopEventLog EventLog { get; }
    private readonly bool _diagnostic;
    private readonly DispatcherQueue _dispatcher;
    private readonly SemaphoreSlim _connectionGate = new(1);
    private readonly DispatcherQueueTimer _timer;
    private readonly WarningTransitionTracker _warnings = new();
    private HttpClient? _http;
    private IMonitoringApi _api = null!;
    private MonitoringSession? _session;
    private CancellationTokenSource? _connectionStop;
    private Task? _connectionTask;
    private MainWindow? _window;
    private TrayService? _tray;
    private bool _rendering = true;
    private bool _refresh = true;
    private bool _connected;
    private string? _address;
    private bool _startupEnabled;
    private Contracts.Sensors.SensorSnapshot? _rendered;

    public PerformanceRecorder Performance { get; } = new();
    private Contracts.Configuration.ServiceConfiguration? _performanceLiveConfiguration;
    internal nint MainWindowHandle => _window is { } window
        ? WinRT.Interop.WindowNative.GetWindowHandle(window) : 0;

    public bool IsDemo { get; }
    internal bool CanRegisterStartup => !DesktopBuild.IsDebug && !IsDemo;
    public AppPreferences Preferences => Settings.Value;
    public AppSettingsViewModel Settings { get; }
    private readonly DashboardViewModel _liveDashboard = new();
    private readonly ConfigurationEditorViewModel _liveConfiguration;
    public DashboardViewModel Dashboard =>
#if TELEMETRY_DEBUG
        Debug.Enabled ? _debugDashboard :
#endif
        _liveDashboard;
    public ConfigurationEditorViewModel Configuration =>
#if TELEMETRY_DEBUG
        Debug.Enabled ? _debugConfiguration :
#endif
        _liveConfiguration;
    private IMonitoringApi CurrentApi =>
#if TELEMETRY_DEBUG
        Debug.Enabled ? _debugApi :
#endif
        _api;
    internal IMonitoringApi SettingsProfileApi => CurrentApi;
    public CancellationToken StopToken => _stop.Token;
    internal long LatestSequence => _session?.Latest?.Sequence ?? 0;
    internal long RenderTicks { get; private set; }
    internal bool TrayAvailable => _tray?.IsAvailable == true;
    internal event Action<string>? OperationCompleted;

    public AppRuntime(bool demo, DispatcherQueue dispatcher, bool diagnostic = false)
    {
#if TELEMETRY_DEBUG
        IsDemo = demo;
#else
        IsDemo = false;
#endif
        _dispatcher = dispatcher;
        _diagnostic = diagnostic;
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DesktopBuild.DataDirectoryName);
        string? migrationError = null;
        if (!diagnostic && CanRegisterStartup)
        {
            try
            {
                LegacyDesktopPreferences.Import(directory);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                migrationError = "Previous RTX Monitor preferences could not be imported: " + error.Message;
            }
        }

        _store = new PreferencesStore(Path.Combine(directory, diagnostic ? "smoke-preferences.json" : demo ? "demo-preferences.json" : "preferences.json"));
        _lastSavedPreferences = _store.Load();
        Settings = new AppSettingsViewModel(_lastSavedPreferences);
        EventLog = new DesktopEventLog(Path.Combine(directory,
            diagnostic ? "smoke-events.json" : demo ? "demo-events.json" : "desktop-events.json"));
        _startupEnabled = Settings.Value.StartWithWindows;
        Settings.Message = migrationError ?? _store.LastError ?? string.Empty;
        Settings.Changed += PreferencesChanged;
        InitializeUpdates(directory);
        if (!DesktopBuild.IsDebug)
        {
            InitializeSafeguard();
        }

        _liveConfiguration = new ConfigurationEditorViewModel(() => _api, recordChange: RecordConfigurationChangeAsync);
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += (_, _) => RenderLatest();
#if TELEMETRY_DEBUG
        InitializeDebugging();
#endif
    }

    public async Task StartAsync(MainWindow window, TrayService tray)
    {
        _window = window;
        _tray = tray;
        window.ThemeRoot.ActualThemeChanged += (_, _) => window.ApplyAppearance(Preferences);
        window.ApplyAppearance(Preferences);
        window.Activated += (_, args) =>
        {
            if (args.WindowActivationState != Microsoft.UI.Xaml.WindowActivationState.Deactivated)
            {
                _ = ShowLastWarningShutdownAsync();
            }
        };

        if (CanRegisterStartup && Preferences.StartWithWindows)
        {
            try
            {
                StartupRegistration.SetEnabled(true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Runtime.InteropServices.COMException)
            {
                Settings.Message = $"Windows startup registration could not be updated: {exception.Message}";
            }
        }

        await ConnectAsync();
        _timer.Start();
        StartUpdates();
    }

    public void Reconnect() => _session?.Reconnect();
    public void SetPinMetric(PinMetric metric) => Settings.PinMetric = metric;
    internal void SetDemoDeviceAvailable(string pluginId, bool available)
    {
#if TELEMETRY_DEBUG
        if (_api is not Demo.DemoMonitoringApi demo)
        {
            throw new InvalidOperationException("Simulated devices are available only in demo mode.");
        }

        demo.SetDeviceAvailable(pluginId, available);
#else
        throw new InvalidOperationException("Simulation is not compiled into this build.");
#endif
    }

    public void SetRendering(bool enabled)
    {
        if (_rendering == enabled)
        {
            return;
        }

        _rendering = enabled;
        _window?.SetRendering(enabled);
        if (enabled)
        {
            _refresh = true;
            RenderLatest();
            _timer.Start();
            _ = ShowLastWarningShutdownAsync();
        }
        else
        {
            _timer.Stop();
        }
    }

    private async void PreferencesChanged(AppPreferences preferences)
    {
        try
        {
            ConfigureUpdates();
            _ = Updates.CheckAsync(false, StopToken);
            _window?.ApplyAppearance(preferences);
            WireViewSettingsWindow?.ApplyAppearance(preferences);
            FanCurveWindow?.ApplyAppearance(preferences);
            AfterburnerWindow?.ApplyAppearance(preferences);
            SafeguardHistory?.ApplyAppearance(preferences);
#if TELEMETRY_DEBUG
            DebugConsole?.ApplyAppearance(preferences);
#endif
            _refresh = true;
            if (CanRegisterStartup && _startupEnabled != preferences.StartWithWindows)
            {
                StartupRegistration.SetEnabled(preferences.StartWithWindows);
                _startupEnabled = preferences.StartWithWindows;
            }
            await SavePreferencesAsync(preferences, StopToken);

            if (_address != preferences.ServiceAddress)
            {
                await ConnectAsync();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Runtime.InteropServices.COMException or OperationCanceledException)
        {
            Settings.Message = exception.Message;
        }
    }

    private async Task SavePreferencesAsync(AppPreferences preferences, CancellationToken token = default)
    {
        await _preferenceSaveGate.WaitAsync(token);
        try
        {
            await _store.SaveAsync(preferences, token);
            var previous = _lastSavedPreferences;
            _lastSavedPreferences = preferences;
            await EventLog.RecordAsync("App settings", previous, preferences);
        }
        finally
        {
            _preferenceSaveGate.Release();
        }
    }

    internal Task RecordConfigurationChangeAsync(Contracts.Configuration.ServiceConfiguration before,
        Contracts.Configuration.ServiceConfiguration after) => EventLog.RecordAsync("Service configuration", before, after);

    private async Task ConnectAsync()
    {
        await _connectionGate.WaitAsync(StopToken);
        try
        {
            _connectionStop?.Cancel();
            if (_connectionTask is not null)
            {
                await _connectionTask;
            }

            _connectionStop?.Dispose();
            _http?.Dispose();
            _address = Preferences.ServiceAddress;
            _http = new HttpClient { BaseAddress = new Uri(_address), Timeout = TimeSpan.FromSeconds(8) };
            _api =
#if TELEMETRY_DEBUG
                IsDemo ? new Demo.DemoMonitoringApi() :
#endif
                new MonitoringApiClient(_http);
            var session = new MonitoringSession(_api);
            _session = session;
            session.StatusChanged += status =>
            {
#if TELEMETRY_DEBUG
                if (Debug.Enabled)
                {
                    return;
                }
#endif
                if (!status.Connected)
                {
                    Performance.MarkDisconnected();
                }
            };
            session.CatalogReceived += Performance.SetCatalog;
            session.ConfigurationReceived += configuration =>
            {
                _performanceLiveConfiguration = configuration.Configuration;
#if TELEMETRY_DEBUG
                if (Debug.Enabled)
                {
                    return;
                }
#endif
                Performance.SetConfiguration(configuration.Configuration);
            };
            session.StatusChanged += status => Dispatch(session, () =>
            {
                var connectedNow = status.Connected && !_connected;
                _connected = status.Connected;

                if (connectedNow)
                {
                    _ = Updates.RefreshServiceAsync(StopToken);
                    _ = ReadLastWarningShutdownAsync();
                }

                _liveDashboard.Connection = status.Message;
                _tray?.SetStatus(status.Message);
                if (!status.Connected && _rendering)
                {
                    _liveDashboard.Disconnect(Preferences);
                }
#if TELEMETRY_DEBUG
                if (!status.Connected)
                {
                    Debug.ReceiveSnapshot(null);
                }

                if (Debug.Enabled)
                {
                    PublishDebugFrame();
                }
#endif

                _refresh = true;
            });
            session.CatalogReceived += catalog => Dispatch(session, () =>
            {
                _liveDashboard.SetCatalog(catalog);
                _liveConfiguration.SetCatalog(catalog);
#if TELEMETRY_DEBUG
                Debug.ReceiveCatalog(catalog);
                if (Debug.Enabled)
                {
                    PublishDebugFrame();
                }
#endif
            });
            session.ConfigurationReceived += configuration => Dispatch(session, () =>
            {
                _liveDashboard.SetConfiguration(configuration);
                _liveConfiguration.Receive(configuration);
#if TELEMETRY_DEBUG
                Debug.ReceiveConfiguration(configuration);
                if (Debug.Enabled)
                {
                    PublishDebugFrame();
                }
#endif
            });
            session.SnapshotReceived += snapshot =>
            {
                if (snapshot.SafeguardExecutions is { } executions)
                {
                    Dispatch(session, () => ReceiveSafeguard(executions));
                }

                var notifications =
#if TELEMETRY_DEBUG
                    Debug.Enabled ? [] :
#endif
                    _warnings.Evaluate(snapshot, DateTimeOffset.UtcNow, Preferences);
#if TELEMETRY_DEBUG
                Dispatch(session, () =>
                {
                Debug.ReceiveSnapshot(snapshot);
                    if (Debug.Enabled)
                    {
                        PublishDebugFrame();
                    }
                });
                if (Debug.Enabled)
                {
                    return;
                }
#endif
                Performance.Accept(snapshot);

                if (notifications.Length > 0)
                {
                    Dispatch(session, () =>
                    {
                        if (Preferences.WarningNotifications
#if TELEMETRY_DEBUG
                            && !Debug.Enabled
#endif
                            )
                        {
                            foreach (var warning in notifications)
                            {
                                ShowNotification(warning.Title, warning.Message);
                            }
                        }
                    });
                }
            };
            _connectionStop = CancellationTokenSource.CreateLinkedTokenSource(StopToken);
            _connectionTask = Task.Run(() => session.RunAsync(_connectionStop.Token), StopToken);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private void Dispatch(MonitoringSession session, Action action) => _dispatcher.TryEnqueue(() =>
    {
        if (_session == session && !StopToken.IsCancellationRequested)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "rtxlens-error.txt"), exception.ToString());
                throw;
            }
        }
    });

    internal void RefreshPresentation() => _refresh = true;

    private void RenderLatest()
    {
        if (!_rendering)
        {
            return;
        }

        RenderTicks++;
        var snapshot =
#if TELEMETRY_DEBUG
            Debug.Enabled ? _debugFrame?.Snapshot :
#endif
            _session?.Latest;
        if (snapshot is not null && (_refresh || !ReferenceEquals(snapshot, _rendered)))
        {
            Dashboard.Apply(snapshot, Preferences, DateTimeOffset.UtcNow, _window?.HardwareDetailsVisible != false);
#if TELEMETRY_DEBUG
            ApplyDebugDeviceConfiguration();
#endif
            if (_window?.ConfigurationVisible == true)
            {
                Configuration.SetAvailableSensors(snapshot);
            }
            _rendered = snapshot;
        }
        else if (!_connected
#if TELEMETRY_DEBUG
            && !Debug.Enabled
#endif
            )
        {
            Dashboard.Disconnect(Preferences);
        }
        else
        {
            Dashboard.RefreshFreshness(DateTimeOffset.UtcNow, Preferences);
        }

        Settings.SetGpuStatusTabRequired(!Dashboard.HasAlternativeGpuStatus);
        WireViewSettingsWindow?.SetPanel(Dashboard.Panels.FirstOrDefault(panel => panel.IsWireView));
        _refresh = false;
    }

    public Task RestartPluginAsync(string id) => PerformAsync(async () =>
    {
        await CurrentApi.RestartPluginAsync(id, StopToken);
        Configuration.Message = $"Restart requested for {id}";
        OperationCompleted?.Invoke(Configuration.Message);
    });

    public Task ReadWireViewDeviceAsync() => PerformAsync(async () =>
    {
        var panel = Dashboard.Panels.FirstOrDefault(item => item.IsWireView);
        var api = CurrentApi;
        var device = panel?.DeviceText;
        panel?.SetDeviceConfigurationStatus("Reading device settings...");
        try
        {
            var configuration = await api.GetWireViewConfigurationAsync(StopToken);
            if (panel is not { IsReporting: true } || panel.DeviceText != device || !ReferenceEquals(api, CurrentApi)
                || !ReferenceEquals(panel, Dashboard.Panels.FirstOrDefault(item => item.IsWireView)))
            {
                return;
            }
#if TELEMETRY_DEBUG
            if (!Debug.Enabled)
            {
                _liveWireViewConfiguration = configuration;
            }
#endif
            panel?.SetDeviceConfiguration(configuration);
            OperationCompleted?.Invoke("WireView device settings read");
        }
        catch (Exception exception) when (exception is HttpRequestException or MonitoringApiException or IOException
            or OperationCanceledException or System.Text.Json.JsonException)
        {
            panel?.SetDeviceConfigurationStatus("Could not read device settings: " + exception.Message);
            throw;
        }
    });

    public Task ClearFaultsAsync(bool status, bool log) => PerformAsync(async () =>
    {
        if (!status && !log)
        {
            return;
        }

        await CurrentApi.ClearWireViewFaultsAsync(new ClearFaultsRequest
        {
            FaultStatusMask = status ? ushort.MaxValue : (ushort)0,
            FaultLogMask = log ? ushort.MaxValue : (ushort)0
        }, StopToken);
        Configuration.Message = "Fault-clear command acknowledged by WireView";
        OperationCompleted?.Invoke(Configuration.Message);
    });

    public Task RefreshDevicesAsync() => PerformAsync(async () =>
    {
        Configuration.SetDevices(await CurrentApi.GetAsusDevicesAsync(StopToken));
        Configuration.Message = "GPU list refreshed";
        OperationCompleted?.Invoke(Configuration.Message);
    });

    public Task RefreshNvidiaDevicesAsync() => PerformAsync(async () =>
    {
        Configuration.SetNvidiaDevices(await CurrentApi.GetNvidiaDevicesAsync(StopToken));
        Configuration.Message = "Desktop GeForce RTX GPU list refreshed";
        OperationCompleted?.Invoke(Configuration.Message);
    });

    public Task RefreshMsiDevicesAsync() => PerformAsync(async () =>
    {
        Configuration.SetMsiDevices(await CurrentApi.GetMsiDevicesAsync(StopToken));
        Configuration.Message = "PSU list refreshed";
        OperationCompleted?.Invoke(Configuration.Message);
    });

    public Task RestartServiceAsync() => PerformAsync(async () =>
    {
        await CurrentApi.RestartServiceAsync(StopToken);
        OperationCompleted?.Invoke("Backend service restart requested");
#if TELEMETRY_DEBUG
        if (Debug.Enabled)
        {
            return;
        }
#endif
        Reconnect();
    });

    private void ShowNotification(string title, string message)
    {
        if (!_diagnostic)
        {
            _tray?.Notify(title, message);
        }
    }

    private async Task PerformAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (Exception exception) when (exception is HttpRequestException or MonitoringApiException or IOException
            or OperationCanceledException or System.Text.Json.JsonException)
        {
            Configuration.Message = exception.Message;
            Settings.Message = exception.Message;
            OperationCompleted?.Invoke(exception.Message);
            ShowNotification("RTX Lens · operation failed", exception.Message);
        }
    }

    public async Task StopAsync()
    {
        Settings.Changed -= PreferencesChanged;
        SaveWindowPlacements();
        _releaseNotesWindow?.Close();
        Performance.Stop();
        _timer.Stop();
        _updateTimer?.Stop();
        Updates.Cancel();
        _updateHttp.Dispose();
        WireViewSettingsWindow?.Close();
        FanCurveWindow?.Close();
        AfterburnerWindow?.CloseAfterConfirmation();
        SafeguardHistory?.Close();
#if TELEMETRY_DEBUG
        StopDebugging();
#endif
        _stop.Cancel();
        if (_connectionTask is not null)
        {
            try { await _connectionTask; }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }
        _http?.Dispose();
        await SavePreferencesAsync(Preferences);
    }
}
