using System.ComponentModel;
using System.Diagnostics;
using RTXLens.WarningUI;
using System.Net.WebSockets;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using RTXLens.App.Platform;
using RTXLens.App.Themes;
using RTXLens.App.ViewModels.Appearance;
using RTXLens.Client.Streaming;
using RTXLens.Contracts.Monitoring;
using RTXLens.Contracts.Warnings;
using RTXLens.Presentation.Notifications;
using RTXLens.Presentation.Settings;

namespace RTXLens.Notifier;

internal sealed partial class NotifierApp : Application
{
    private readonly CancellationTokenSource _stop = new();
    private readonly WarningTransitionTracker _tracker = new();
    private readonly HashSet<string> _visibleEpisodes = [];
    private readonly string _directory;
    private readonly EventWaitHandle _previewEvent;
    private AppPreferences _prefs = new();
    private WarningSnapshot? _latest;
    private readonly HashSet<string> _soundingEpisodes = [];
    private bool _previewPlaying;
    private IDisposable? _previewTimeout;
    private WarningPopup? _popup;
    private WarningPopup? _preview;
    private CancellationTokenSource? _connection;
    private RegisteredWaitHandle? _previewSignal;
    private AppearanceViewModel _appearance = null!;
    private PreferencesStore _preferences = null!;
    private FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _reload = new() { Interval = TimeSpan.FromMilliseconds(200) };

    public NotifierApp() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        DesktopBuild.DataDirectoryName, "Avalonia"), Program.PreviewSignal) { }

    internal NotifierApp(string directory, EventWaitHandle previewEvent)
    {
        _directory = directory;
        _previewEvent = previewEvent;
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();

        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        desktop.Exit += (_, _) =>
        {
            _stop.Cancel();
            _watcher?.Dispose();
            _reload.Stop();
            _previewTimeout?.Dispose();
            _previewSignal?.Unregister(null);
            WarningSoundPlayer.Stop();
        };

        Directory.CreateDirectory(_directory);

        _preferences = new PreferencesStore(Path.Combine(_directory, "preferences.json"));
        _appearance = new AppearanceViewModel(this, new AppearanceStore(Path.Combine(_directory, "appearance.json")));
        _watcher = new FileSystemWatcher(_directory, "*.json") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };

        _watcher.Renamed += (_, e) => ScheduleReload(e.Name);
        _watcher.Changed += (_, e) => ScheduleReload(e.Name);
        _watcher.Created += (_, e) => ScheduleReload(e.Name);
        _watcher.EnableRaisingEvents = true;

        _reload.Tick += async (_, _) => { _reload.Stop(); await LoadSettingsAsync(); };

        Dispatcher.UIThread.Post(async void () =>
        {
            await LoadSettingsAsync();

            _previewSignal = ThreadPool.RegisterWaitForSingleObject(_previewEvent,
                (_, _) => Dispatcher.UIThread.Post(async void () => { await LoadSettingsAsync(); Preview(); }), null, -1, false);

            await ListenAsync();
        });
    }

    private void ScheduleReload(string? name)
    {
        if (name is "preferences.json" or "appearance.json")
        {
            Dispatcher.UIThread.Post(() => { _reload.Stop(); _reload.Start(); });
        }
    }

    private async Task LoadSettingsAsync()
    {
        var old = _prefs;
        var next = _preferences.Load();

        if (_preferences.LastError is null)
        {
            _prefs = next;
        }

        if (old.ServiceAddress != _prefs.ServiceAddress)
        {
            if (_connection is not null)
            {
                await _connection.CancelAsync();
            }
        }

        await _appearance.LoadAsync(_prefs.Theme);

        if (!_prefs.PlayWarningSound)
        {
            StopLiveSound();
            StopPreviewSound();
        }
        else if ((_soundingEpisodes.Count > 0 || _previewPlaying) &&
                 (old.LoopWarningSound != _prefs.LoopWarningSound || old.WarningSound != _prefs.WarningSound ||
                  old.WarningSoundVolume != _prefs.WarningSoundVolume))
        {
            Play();
        }

        if (!_prefs.WarningNotifications)
        {
            _popup?.Close();
        }

        if (_latest is not null)
        {
            Receive(_latest);
        }
    }

    private async Task ListenAsync()
    {
        var retry = 1;

        while (!_stop.IsCancellationRequested)
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            _connection = cancel;

            try
            {
                var endpoint = new Uri(_prefs.ServiceAddress);
                if (!endpoint.IsLoopback)
                {
                    endpoint = new Uri(new AppPreferences().ServiceAddress);
                }

                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);

                var url = new UriBuilder(new Uri(endpoint, ServiceRoutes.WarningStream)) { Scheme = endpoint.Scheme == "https" ? "wss" : "ws" };
                await socket.ConnectAsync(url.Uri, cancel.Token);

                await foreach (var feed in SensorFrameReader.ReadWarningsAsync(socket, cancel.Token))
                {
                    retry = 1;
                    Receive(feed);
                }
            }
            catch (Exception error) when (error is WebSocketException or IOException or InvalidDataException or JsonException or OperationCanceledException)
            {
                if (!cancel.IsCancellationRequested && retry == 1)
                {
                    Log("Warning stream disconnected", error);
                }
            }
            finally
            {
                _connection = null;
                _latest = null;

                StopLiveSound();

                _popup?.Update("Unable to verify warning", "The service disconnected. Waiting for current readings.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(retry), _stop.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            retry = Math.Min(15, retry * 2);
        }
    }

    private void Receive(WarningSnapshot feed)
    {
        _latest = feed;

        var wasSounding = _soundingEpisodes.Count > 0;
        _soundingEpisodes.IntersectWith(feed.States.Where(s => s.Phase == WarningPhase.Active).Select(s => s.EpisodeId));

        if (wasSounding && (_soundingEpisodes.Count == 0 || !feed.NotificationsEnabled))
        {
            StopLiveSound();
        }

        var due = _tracker.TakeDue(feed, _prefs.WarningNotifications || _prefs.PlayWarningSound);
        if (!feed.NotificationsEnabled || !_prefs.WarningNotifications)
        {
            _popup?.Close();
        }

        if (due.Length > 0)
        {
            if (_prefs.PlayWarningSound)
            {
                StopPreviewSound();
                if (_soundingEpisodes.Count == 0 || !_prefs.LoopWarningSound)
                {
                    Play();
                }

                _soundingEpisodes.UnionWith(due.Select(s => s.EpisodeId));
            }

            if (_prefs.WarningNotifications)
            {
                _visibleEpisodes.UnionWith(due.Select(s => s.EpisodeId));
                if (_popup is null)
                {
                    _popup = CreatePopup();
                    _popup.Closed += (_, _) => { _popup = null; _visibleEpisodes.Clear(); if (_prefs.WarningNotifications)
                        {
                            StopLiveSound();
                        }
                    };
                }
            }
        }

        if (_popup is not { } popup)
        {
            return;
        }

        var rows = feed.States.Where(s => _visibleEpisodes.Contains(s.EpisodeId)).ToArray();
        _visibleEpisodes.IntersectWith(rows.Select(s => s.EpisodeId));

        if (rows.Length == 0)
        {
            popup.Close(); return;
        }

        popup.Update(rows);

        if (!popup.IsVisible)
        {
            popup.Show();
        }
    }

    private void Preview()
    {
        _previewTimeout?.Dispose();
        _preview?.Close();

        StopPreviewSound();

        if (_prefs.PlayWarningSound && _soundingEpisodes.Count == 0)
        {
            _previewPlaying = true;
            Play();
        }

        if (_prefs.WarningNotifications)
        {
            var preview = _preview = CreatePopup();
            preview.Update("Test warning", "P1: 9.2 A", "ROG Astral · Preview");

            preview.Closed += (_, _) => { _preview = null; StopPreviewSound(); };
            preview.Show();
        }

        _previewTimeout = DispatcherTimer.RunOnce(() => { _preview?.Close(); StopPreviewSound(); }, TimeSpan.FromSeconds(8));
    }

    private void StopPreviewSound()
    {
        if (!_previewPlaying)
        {
            return;
        }

        WarningSoundPlayer.Stop();
        _previewPlaying = false;
    }

    private void StopLiveSound()
    {
        if (!_previewPlaying)
        {
            WarningSoundPlayer.Stop();
        }

        _soundingEpisodes.Clear();
    }

    private WarningPopup CreatePopup()
    {
        var desktop = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Desktop", "RTXLens.App.exe"));

        return new WarningPopup(File.Exists(desktop)
            ? () =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(desktop, "--warnings") { UseShellExecute = true });
                }
                catch (Win32Exception error)
                {
                    Log("Dashboard did not open", error);
                }
            } : null);
    }

    private void Play()
    {
        try
        {
            WarningSoundPlayer.Play(_prefs, _directory, _prefs.LoopWarningSound);
        }
        catch (IOException error)
        {
            Log("Warning sound unavailable", error);
        }
    }

    private void Log(string message, Exception error)
    {
        try
        {
            var path = Path.Combine(_directory, "notifier.log");
            if (File.Exists(path) && new FileInfo(path).Length > 65536)
            {
                File.Move(path, path + ".previous", true);
            }

            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {message} ({error.GetType().Name}, {error.HResult:X8}){Environment.NewLine}");
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
        }
    }
}
