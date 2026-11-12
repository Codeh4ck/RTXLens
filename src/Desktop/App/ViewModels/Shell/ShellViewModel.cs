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
    private readonly CancellationTokenSource _stop = new();
    private readonly DispatcherTimer _displayTimer;
    private SensorSnapshot? _displayedSnapshot;
    private DateTimeOffset _nextFreshnessCheck;
    private bool _connected;
    private readonly Lock _recordingModeLock = new();
    private bool _started;
    private bool _disposed;

    public ShellViewModel(PreferencesStore store, AppearanceViewModel appearance, Func<Uri, HttpClient>? createHttpClient = null)
    {
        _store = store;
        _createHttpClient = createHttpClient ?? (address => new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(8) });
        Appearance = appearance;
        Preferences = store.Load();

        _configuration = new ConfigurationEditorViewModel(() => _liveApi);
        InitializeSettings();
        Connect(Preferences.ServiceAddress);
        _displayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _displayTimer.Tick += (_, _) => RefreshPresentation();
    }

    private void Connect(string address)
    {
        _connectionStop?.Cancel();
        _connectionStop?.Dispose();
        _connectionStop = CancellationTokenSource.CreateLinkedTokenSource(StopToken);
        _http?.Dispose();
        _http = _createHttpClient(new Uri(address));
        _liveApi = new MonitoringApiClient(_http);
        _liveCatalog = null;
        _liveConfiguration = null;
        RaiseApiChanged();
        Logs = new ServiceLogClient(_http);
        var session = _session = new MonitoringSession(_liveApi);

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
            UpdateLiveRecorder(() => Recorder.SetCatalog(catalog));
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
            UpdateLiveRecorder(() => Recorder.SetConfiguration(configuration.Configuration));
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
                    if (!IsSimulation)
                    {
                        Recorder.Accept(snapshot);
                    }
                }

                QueueWarningNotifications(session, snapshot);
            }
        };

        if (_started)
        {
            StartConnection();
        }
    }

    private void UpdateLiveRecorder(Action update)
    {
        lock (_recordingModeLock)
        {
            if (!IsSimulation)
            {
                update();
            }
        }
    }

    public DashboardViewModel Dashboard { get; } = new();
    public PerformanceRecorder Recorder { get; } = new();
    private readonly ConfigurationEditorViewModel _configuration;

    public ConfigurationEditorViewModel Configuration =>
#if TELEMETRY_DEBUG
        _simulationConfiguration ??
#endif
        _configuration;
    public IMonitoringApi Api =>
#if TELEMETRY_DEBUG
        (IMonitoringApi?)Volatile.Read(ref _simulationApi) ??
#endif
        _liveApi;

    public bool IsSimulation => !ReferenceEquals(Api, _liveApi);

    public bool CanOpenWireViewSettings =>
#if TELEMETRY_DEBUG
        !IsSimulation || Simulation.Plugins.Any(plugin => plugin is { Id: "wireview-pro2", IsSimulated: true });
#else
        true;
#endif

    private void RaiseApiChanged()
    {
        OnPropertyChanged(nameof(Api));
        OnPropertyChanged(nameof(IsSimulation));
        OnPropertyChanged(nameof(CanOpenWireViewSettings));
    }
    public ServiceLogClient Logs { get; private set; } = null!;
    public AppearanceViewModel Appearance { get; }
    public CancellationToken StopToken => _stop.Token;
    public AppPreferences Preferences { get; private set; }
    public bool ShowHardware { get; set; } = true;
    public bool ShowConfiguration { get; set; }
    public bool ShowFanCurve { get; set; }

    [ObservableProperty]
    public partial string OperationStatus { get; private set; } = "Live monitoring";

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

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
        _ = Task.Run(() => session.RunAsync(token), token);
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
        var detailPanelId = ShowFanCurve ? "msi-psu" : Dashboard.SelectedPanel?.Id;
        var updateDetails = ShowFanCurve || ShowHardware && detailPanelId is not null;
        Dashboard.Apply(snapshot, Preferences, now, updateDetails, detailPanelId);

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

    private void Post(MonitoringSession session, Action update)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed && ReferenceEquals(_session, session))
            {
                update();
            }
        });
    }

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
        _connectionStop?.Dispose();
        _updateTimer.Stop();
        Updates.Cancel();
        _updateHttp.Dispose();
        _http.Dispose();
    }
}
