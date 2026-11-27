using RTXLens.Plugins.Msi.Contracts;
using RTXLens.Plugins.WireView.Contracts;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RTXLens.App.ViewModels.Appearance;
using RTXLens.Client.Http;
using RTXLens.Client.Streaming;
using RTXLens.Contracts.Sensors;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Discovery;
using RTXLens.Presentation.Configuration;
using RTXLens.Presentation.Performance;
using RTXLens.Presentation.Settings;
using RTXLens.Presentation.Telemetry;

namespace RTXLens.App.ViewModels.Shell;

internal sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly PreferencesStore _store;
    private readonly Func<Uri, HttpClient> _createHttpClient;
    private HttpClient _http = null!;
    private MonitoringApiClient _liveApi = null!;
    private PluginCatalog? _liveCatalog;
    private ConfigurationSnapshot? _liveConfiguration;
    private string _liveConnection = "Connecting to monitoring service…";
    private MonitoringSession _session = null!;
    private CancellationTokenSource? _connectionStop;
    private Task _connectionTask = Task.CompletedTask;
    private readonly CancellationTokenSource _stop = new();
    private readonly DispatcherTimer _displayTimer;
    private SensorSnapshot? _displayedSnapshot;
    private DateTimeOffset _nextFreshnessCheck;
    private bool _connected;
    private readonly Lock _recordingModeLock = new();
    private bool _started;
    private bool _disposed;

    internal MonitoringApiClient HistoryApi => _liveApi;
    internal MonitoringApiClient WarningProfileApi => _liveApi;

    internal async Task<DeviceWarningProfileCatalog> LoadWarningProfilesAsync(IMonitoringApi api)
    {
#if TELEMETRY_DEBUG
        var simulationApi = _simulationApi;
        var request = ++_warningProfileRequest;
#endif

        var catalog = await api.GetDeviceWarningProfilesAsync(StopToken);
        if (catalog.Profiles is null || catalog.Targets is null || catalog.Defaults is null || catalog.AstralDefaults is null)
        {
            throw new InvalidDataException("The monitoring service returned an incomplete warning profile catalog.");
        }

#if TELEMETRY_DEBUG
        if (simulationApi is not null && ReferenceEquals(simulationApi, _simulationApi) && ReferenceEquals(api, _liveApi) && request == _warningProfileRequest && !StopToken.IsCancellationRequested)
        {
            var count = catalog.Profiles.Count(profile => profile.Enabled);
            Simulation.SetWarningProfiles(catalog, count == 1 ? "1 enabled saved warning profile loaded." : $"{count} enabled saved warning profiles loaded.");
        }
#endif

        return catalog;
    }
    public ServiceLogClient Logs { get; private set; } = null!;
    public AppearanceViewModel Appearance { get; }
    public CancellationToken StopToken => _stop.Token;
    internal string PreferencesDirectory => _store.DirectoryPath;
    public AppPreferences Preferences { get; private set; }
    public bool ShowHardware { get; set; } = true;
    public bool ShowConfiguration { get; set; }
    public bool ShowFanCurve { get; set; }

    [ObservableProperty]
    public partial string OperationStatus { get; private set; } = "Live monitoring";

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    public ShellViewModel(PreferencesStore store, AppearanceViewModel appearance, Func<Uri, HttpClient>? createHttpClient = null)
    {
        _store = store;
        _createHttpClient = createHttpClient ?? (address => new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(8) });
        Appearance = appearance;
        Preferences = store.Load();

        if (store.LastError is { } error)
        {
            OperationStatus = error;
        }

        Configuration = new ConfigurationEditorViewModel(() => _liveApi);

        InitializeSettings();
        Connect(Preferences.ServiceAddress);

        _displayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _displayTimer.Tick += (_, _) => RefreshPresentation();
    }

    private void Connect(string address)
    {
        StopConnection();

        _connectionStop = CancellationTokenSource.CreateLinkedTokenSource(StopToken);
        _http = _createHttpClient(new Uri(address));
        _liveApi = new MonitoringApiClient(_http);
        _liveCatalog = null;
        _liveConfiguration = null;

        RaiseApiChanged();

        Logs = new ServiceLogClient(_http);
        var session = new MonitoringSession(_liveApi);

        lock (_recordingModeLock)
        {
            _session = session;
        }

        session.StatusChanged += status => Post(session, () =>
        {
            var newlyConnected = status.Connected && !_connected;
            _connected = status.Connected;
            _liveConnection = status.Message;

            if (IsSimulation)
            {
                return;
            }

            Dashboard.Connection = status.Message;

            if (!status.Connected)
            {
                _displayedSnapshot = null;
                Dashboard.Disconnect(Preferences);
            }
            else if (newlyConnected)
            {
                _ = Updates.RefreshServiceAsync(StopToken);

                if (ShowConfiguration)
                {
                    _ = RefreshDeviceListsAsync();
                }

                ReadLastWarningShutdown();
            }
        });

        session.CatalogReceived += catalog =>
        {
            UpdateLiveRecorder(session, () => Recorder.SetCatalog(catalog));

            Post(session, () =>
            {
                _liveCatalog = catalog;

#if TELEMETRY_DEBUG
                _simulation?.ReceiveCatalog(catalog);
#endif
                if (!IsSimulation)
                {
                    Dashboard.SetCatalog(catalog);
                    Configuration.SetCatalog(catalog);
                }
            });
        };

        session.ConfigurationReceived += configuration =>
        {
            UpdateLiveRecorder(session, () => Recorder.SetConfiguration(configuration.Configuration));

            Post(session, () =>
            {
                _liveConfiguration = configuration;

#if TELEMETRY_DEBUG
                _simulation?.ReceiveConfiguration(configuration);
#endif
                if (!IsSimulation)
                {
                    Dashboard.SetConfiguration(configuration);
                    Configuration.Receive(configuration);
                }
            });
        };

        session.SnapshotReceived += snapshot =>
        {
            if (ReferenceEquals(Volatile.Read(ref _session), session))
            {
                lock (_recordingModeLock)
                {
                    if (ReferenceEquals(_session, session) && !IsSimulation)
                    {
                        Recorder.Accept(snapshot);
                    }
                }

                QueueSafeguardNotifications(session, snapshot);
            }
        };

        if (_started)
        {
            StartConnection();
        }
    }

    private void UpdateLiveRecorder(MonitoringSession session, Action update)
    {
        lock (_recordingModeLock)
        {
            if (ReferenceEquals(_session, session) && !IsSimulation)
            {
                update();
            }
        }
    }

    public DashboardViewModel Dashboard { get; } = new();
    public PerformanceRecorder Recorder { get; } = new();

    public ConfigurationEditorViewModel Configuration =>
#if TELEMETRY_DEBUG
        _simulationConfiguration ??
#endif
        field;
    public IMonitoringApi Api =>
#if TELEMETRY_DEBUG
        (IMonitoringApi?)Volatile.Read(ref _simulationApi) ??
#endif
        _liveApi;

    public bool IsSimulation => !ReferenceEquals(Api, _liveApi);

    internal IEnumerable<string> SimulatedWarningSources
    {
        get
        {

#if TELEMETRY_DEBUG
            if (IsSimulation)
            {
                return Simulation.Plugins.Where(plugin => plugin is
                    { IsSimulated: true, Installed: true, Enabled: true, State: Contracts.Enums.PluginState.Ready }).Select(plugin => plugin.Id);
            }
#endif
            return [];
        }
    }

    public bool CanOpenWireViewSettings =>
#if TELEMETRY_DEBUG
        !IsSimulation || Simulation.Plugins.Any(plugin => plugin is { Id: WireViewIdentity.Id, IsSimulated: true });
#else
        true;
#endif

    private void RaiseApiChanged()
    {
        OnPropertyChanged(nameof(Api));
        OnPropertyChanged(nameof(IsSimulation));
        OnPropertyChanged(nameof(SimulatedWarningSources));
        OnPropertyChanged(nameof(CanOpenWireViewSettings));
    }

    public async Task StartAsync()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _displayTimer.Start();

        StartConnection();

        if (OperatingSystem.IsWindows())
        {
            _ = LoadSafeguardHistoryAsync();
        }

        await Appearance.LoadAsync(Preferences.Theme);

        _updateTimer.Start();
        _ = Updates.CheckAsync(false, StopToken);
    }

    private void StartConnection()
    {
        var session = _session;
        var token = _connectionStop!.Token;

        _connectionTask = Task.Run(async () =>
        {
            try
            {
                await session.RunAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // The session was replaced or the app is closing.
            }
            catch (Exception error)
            {
                Post(session, () =>
                {
                    _liveConnection = $"Monitoring stopped. {error.Message}";
                    OperationStatus = _liveConnection;
                    if (!IsSimulation)
                    {
                        Dashboard.Connection = _liveConnection;
                    }
                });
            }
        }, token);
    }

    private void StopConnection()
    {
        if (_connectionStop is { } stop)
        {
            stop.Cancel();

            _ = ReleaseConnectionAsync(_connectionTask, stop, _http);
            _connectionStop = null;
        }
    }

    private static async Task ReleaseConnectionAsync(Task connection, CancellationTokenSource stop, HttpClient client)
    {
        try
        {
            await connection.ConfigureAwait(false);
        }
        finally
        {
            stop.Dispose();
            client.Dispose();
        }
    }

    public void SetVisible(bool visible)
    {
        if (visible)
        {
            _displayTimer.Start();
            RefreshPresentation(force: true);
        }
        else
        {
            _displayTimer.Stop();
        }
    }

    public void RefreshPresentation(bool force = false)
    {
#if TELEMETRY_DEBUG
        var snapshot = IsSimulation ? _simulatedSnapshot : _session.Latest;
#else
        var snapshot = _session.Latest;
#endif
        if ((!_connected && !IsSimulation) || snapshot is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (!force && ReferenceEquals(snapshot, _displayedSnapshot))
        {
            if (now >= _nextFreshnessCheck)
            {
                Dashboard.RefreshFreshness(now, Preferences);
                _nextFreshnessCheck = now.AddSeconds(1);
            }

            return;
        }

        _displayedSnapshot = snapshot;
        _nextFreshnessCheck = now.AddSeconds(1);

        var detailPanelId = ShowFanCurve ? MsiIdentity.Id : Dashboard.SelectedPanel?.Id;
        var updateDetails = ShowFanCurve || (ShowHardware && detailPanelId is not null);

        Dashboard.Apply(snapshot, Preferences, now, updateDetails, detailPanelId);

        if (Dashboard.Panels.FirstOrDefault(panel => panel.IsWireView) is { IsReporting: true, CanReadDevice: true, DeviceConfiguration: null } wireView
            && CanOpenWireViewSettings && _connectionStop is { } connection)
        {
            _ = wireView.EnsureDeviceConfigurationAsync(Api.GetWireViewConfigurationAsync, now, connection.Token);
        }

        if (ShowConfiguration)
        {
            Configuration.SetAvailableSensors(snapshot);
        }
    }

    public void SetPinMetric(PinMetric metric)
    {
        Preferences = Preferences with { PinMetric = metric };
        RefreshPresentation(force: true);
    }

    public void SetPreferences(AppPreferences preferences)
    {
        Preferences = preferences;

        Updates.Configure(preferences.AutomaticallyCheckForUpdates, preferences.UpdateFrequency);
        Appearance.SetTheme(preferences.Theme);
        ApplyVisualPreferences(preferences);
        PreferencesChanged?.Invoke(preferences);
        RefreshPresentation(force: true);
    }

    public Task SavePreferencesAsync() => RunAsync(() => PersistPreferencesAsync(Preferences), "Settings saved.");

    [RelayCommand]
    private Task RestartPluginAsync(string id) => RunAsync(() => Api.RestartPluginAsync(id, StopToken), "Plugin restarted.");

    public Task RestartServiceAsync() => RunAsync(() => Api.RestartServiceAsync(StopToken), "Backend service restart requested.");

    public async Task RunAsync(Func<Task> action, string? success = null, Action<string>? showError = null)
    {
        if (_disposed || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await action();
            if (success is not null)
            {
                OperationStatus = success;
            }
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException
            or MonitoringApiException or OperationCanceledException or ArgumentException or InvalidDataException or NotSupportedException
            or System.Text.Json.JsonException or System.Runtime.InteropServices.COMException or System.Security.SecurityException)
        {
            OperationStatus = error.Message;
            showError?.Invoke(error.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Post(MonitoringSession session, Action update) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed && ReferenceEquals(_session, session))
            {
                update();
            }
        });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _displayTimer.Stop();

#if TELEMETRY_DEBUG
        StopSimulation();
#endif
        _stop.Cancel();
        StopConnection();

        _updateTimer.Stop();

        Updates.Cancel();
        _updateHttp.Dispose();
    }
}
