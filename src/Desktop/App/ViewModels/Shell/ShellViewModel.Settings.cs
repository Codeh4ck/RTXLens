using System.Net;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using Avalonia;
using Avalonia.Threading;
using RTXLens.App.Platform;
using RTXLens.App.Platform.Startup;
using RTXLens.App.Services;
using RTXLens.Client.Http;
using RTXLens.Client.Streaming;
using RTXLens.Client.Updates;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Sensors;
using RTXLens.Presentation.Configuration;
using RTXLens.Presentation.Notifications;
using RTXLens.Presentation.Settings;
using RTXLens.Presentation.Updates;

namespace RTXLens.App.ViewModels.Shell;

internal sealed partial class ShellViewModel
{
    private readonly HttpClient _updateHttp = new() { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 131072 };
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly WarningTransitionTracker _warnings = new();
    private readonly SemaphoreSlim _preferencesGate = new(1);
    private readonly HashSet<(IMonitoringApi Api, string Plugin)> _discoveryRequests = [];
    private AppPreferences _savedPreferences = null!;
    private bool _readingShutdown;
    private WarningShutdownReason? _warningShutdown;

    public UpdateViewModel Updates { get; private set; } = null!;
    internal DesktopEventLog LocalEvents { get; private set; } = null!;
    public event Action<AppPreferences>? PreferencesChanged;
    public event Action<WarningNotification>? WarningRaised;
    public event Action? WarningShutdownAvailable;
    public WarningShutdownReason? WarningShutdown => _warningShutdown;

    public static bool CanRegisterStartup
    {
        get
        {
            if (!OperatingSystem.IsWindows() || DesktopBuild.IsDebug)
            {
                return false;
            }

            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    private void InitializeSettings()
    {
        _savedPreferences = Preferences;
        LocalEvents = new DesktopEventLog(Path.Combine(_store.DirectoryPath, "desktop-events.json"));
        var version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? "unknown";
        Updates = new UpdateViewModel(version, new UpdateManifestClient(_updateHttp).GetAsync,
            async token => (await Api.GetVersionAsync(token))?.Version,
            new UpdateStateStore(Path.Combine(_store.DirectoryPath, "updates.json")));
        Updates.Configure(Preferences.AutomaticallyCheckForUpdates, Preferences.UpdateFrequency);
        _updateTimer.Tick += async (_, _) => await Updates.CheckAsync(false, StopToken);
        ApplyVisualPreferences(Preferences);
    }

    public async Task<bool> ChangePreferencesAsync(Func<AppPreferences, AppPreferences> change)
    {
        try
        {
            await _preferencesGate.WaitAsync(StopToken);
            try
            {
                var previous = Preferences;
                var next = change(previous);
                if (next.ServiceAddress != previous.ServiceAddress)
                {
                    next = next with { ServiceAddress = NormalizeServiceAddress(next.ServiceAddress) };
                }

                next.Validate();
                if (next == previous)
                {
                    return true;
                }

                var changedStartup = OperatingSystem.IsWindows() && CanRegisterStartup && next.StartWithWindows != previous.StartWithWindows;
                if (changedStartup && OperatingSystem.IsWindows())
                {
                    StartupRegistration.SetEnabled(next.StartWithWindows);
                }

                try
                {
                    await _store.SaveAsync(next, StopToken);
                }
                catch
                {
                    if (changedStartup && OperatingSystem.IsWindows())
                    {
                        StartupRegistration.SetEnabled(previous.StartWithWindows);
                    }

                    throw;
                }

                SetPreferences(next);
                if (next.ServiceAddress != previous.ServiceAddress)
                {
                    Reconnect();
                }

                await RecordSavedPreferencesAsync(next);
                OperationStatus = "Settings saved.";
                if (_started && next.AutomaticallyCheckForUpdates && !previous.AutomaticallyCheckForUpdates)
                {
                    _ = Updates.CheckAsync(false, StopToken);
                }

                return true;
            }
            finally
            {
                _preferencesGate.Release();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or System.Runtime.InteropServices.COMException or System.Security.SecurityException or OperationCanceledException)
        {
            OperationStatus = error.Message;
            return false;
        }
    }

    private async Task PersistPreferencesAsync(AppPreferences preferences)
    {
        await _preferencesGate.WaitAsync(StopToken);
        try
        {
            if (preferences != Preferences)
            {
                return;
            }

            await _store.SaveAsync(preferences, StopToken);
            await RecordSavedPreferencesAsync(preferences);
        }
        finally
        {
            _preferencesGate.Release();
        }
    }

    private async Task RecordSavedPreferencesAsync(AppPreferences preferences)
    {
        var previous = _savedPreferences;
        _savedPreferences = preferences;
        await LocalEvents.RecordAsync("App settings",
            previous with { WindowWidth = 0, WindowHeight = 0, WindowPlacements = null },
            preferences with { WindowWidth = 0, WindowHeight = 0, WindowPlacements = null });
    }

    private static void ApplyVisualPreferences(AppPreferences preferences)
    {
        if (Application.Current is not { } application)
        {
            return;
        }

        application.Resources["DesktopFontSize"] = 14 * preferences.TextScale;
        application.Resources["DesktopHeadingFontSize"] = 24 * preferences.TextScale;
        application.Resources["DesktopCaptionFontSize"] = 12 * preferences.TextScale;
    }

    private static string NormalizeServiceAddress(string input)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var address)
            || address.Scheme is not ("http" or "https") || address.UserInfo.Length > 0
            || address.Query.Length > 0 || address.Fragment.Length > 0)
        {
            throw new ArgumentException("Enter a valid HTTP or HTTPS service address.");
        }

        return address.AbsoluteUri.TrimEnd('/') + "/";
    }

    public void Reconnect()
    {
        _connected = false;
        _displayedSnapshot = null;
        _warningShutdown = null;
        Dashboard.Disconnect(Preferences);
        Dashboard.Connection = "Connecting to monitoring service…";
        Configuration.SetDevices([]);
        Configuration.SetNvidiaDevices([]);
        Configuration.SetMsiDevices([]);
        Connect(Preferences.ServiceAddress);
    }

    public Task RefreshDeviceListsAsync()
    {
        if ((!_connected && !IsSimulation) || _disposed)
        {
            return Task.CompletedTask;
        }

        return Task.WhenAll(Configuration.Plugins.Where(plugin => plugin.IsAsus || plugin.IsNvidia || plugin.IsMsi)
            .Select(plugin => RefreshDeviceListAsync(plugin, automatic: true)));
    }

    public async Task RefreshDeviceListAsync(PluginSettingsEditorViewModel plugin, bool automatic = false)
    {
        var api = Api;
        var request = (api, plugin.Id);
        if (!_discoveryRequests.Add(request))
        {
            return;
        }

        try
        {
            if (plugin.IsAsus)
            {
                var devices = await api.GetAsusDevicesAsync(StopToken);
                if (!_disposed && ReferenceEquals(Api, api))
                {
                    Configuration.SetDevices(devices);
                }
            }
            else if (plugin.IsNvidia)
            {
                var devices = api is MonitoringApiClient liveApi
                    ? await liveApi.GetNvidiaDevicesAsync(refresh: !automatic, StopToken)
                    : await api.GetNvidiaDevicesAsync(StopToken);
                if (!_disposed && ReferenceEquals(Api, api))
                {
                    Configuration.SetNvidiaDevices(devices);
                }
            }
            else if (plugin.IsMsi)
            {
                var devices = await api.GetMsiDevicesAsync(StopToken);
                if (!_disposed && ReferenceEquals(Api, api))
                {
                    Configuration.SetMsiDevices(devices);
                }
            }
        }
        catch (MonitoringApiException error) when (automatic && error.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable)
        {
            // Older services only offer enumeration for running plugins.
        }
        catch (Exception error) when (automatic && error is HttpRequestException or MonitoringApiException or IOException
            or JsonException or OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine(error);
        }
        finally
        {
            _discoveryRequests.Remove(request);
        }
    }

    private void QueueWarningNotifications(MonitoringSession session, SensorSnapshot snapshot)
    {
        if (snapshot.Warnings is null && snapshot.SafeguardExecutions is null)
        {
            return;
        }

        Post(session, () =>
        {
            if (IsSimulation)
            {
                return;
            }

            if (OperatingSystem.IsWindows() && snapshot.SafeguardExecutions is { } notices)
            {
                ReceiveSafeguard(notices);
            }

            foreach (var warning in _warnings.Evaluate(snapshot, DateTimeOffset.UtcNow, Preferences))
            {
                WarningRaised?.Invoke(warning);
            }
        });
    }

    private async void ReadLastWarningShutdown()
    {
        if (_readingShutdown)
        {
            return;
        }

        var api = Api;
        _readingShutdown = true;
        try
        {
            var reason = await api.GetWarningShutdownAsync(StopToken);
            if (!_disposed && ReferenceEquals(Api, api) && reason is not null)
            {
                _warningShutdown = reason;
                WarningShutdownAvailable?.Invoke();
            }
        }
        catch (Exception error) when (error is HttpRequestException or MonitoringApiException or JsonException or OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine(error);
        }
        finally
        {
            _readingShutdown = false;
        }
    }

    public Task AcknowledgeWarningShutdownAsync(WarningShutdownReason reason) => RunAsync(async () =>
    {
        if (_warningShutdown != reason)
        {
            return;
        }

        await Api.AcknowledgeWarningShutdownAsync(reason.Id, StopToken);
        _warningShutdown = null;
    });
}
