using System.Collections.ObjectModel;
using RTXLens.Contracts.Updates;
using RTXLens.Presentation.Infrastructure;

namespace RTXLens.Presentation.Updates;

public sealed class UpdateViewModel : ObservableObject
{
    private readonly Func<CancellationToken, Task<ReleaseManifest>> _fetch;
    private readonly Func<CancellationToken, Task<string?>> _serviceVersion;
    private readonly UpdateStateStore _store;
    private readonly UpdateCheckState _state;
    private readonly TimeProvider _clock;
    private CancellationTokenSource? _active;
    private DateTimeOffset? _lastAttempt;
    private bool _startup = true;
    private bool _enabled = true;
    private UpdateFrequency _frequency;
    private string? _installedService;

    public UpdateViewModel(string desktopVersion, Func<CancellationToken, Task<ReleaseManifest>> fetch,
        Func<CancellationToken, Task<string?>> serviceVersion, UpdateStateStore store, TimeProvider? clock = null)
    {
        DesktopVersion = desktopVersion;
        _fetch = fetch;
        _serviceVersion = serviceVersion;
        _store = store;
        _state = store.Load();
        _clock = clock ?? TimeProvider.System;
    }

    public string DesktopVersion { get; }
    public string ServiceVersion => _installedService ?? "Version unavailable";
    public ObservableCollection<UpdateNotice> Available { get; } = [];
    public bool HasUpdates => Available.Count != 0;
    public string Summary => Available.Count == 1 ? "Update available" : $"{Available.Count} updates available";
    public string LastChecked => _state.LastSuccessfulCheck?.ToLocalTime().ToString("g") ?? "Never";

    public string Status
    {
        get;
        private set => Set(ref field, value);
    } = "Updates have not been checked yet.";

    public bool Checking
    {
        get;
        private set => Set(ref field, value);
    }

    public void Configure(bool enabled, UpdateFrequency frequency)
    {
        if (_enabled != enabled)
        {
            _active?.Cancel();
            _startup = enabled;
        }

        _enabled = enabled;
        _frequency = frequency;
    }

    public static DateTimeOffset NextCheck(DateTimeOffset last, UpdateFrequency frequency) => frequency switch
    {
        UpdateFrequency.Weekly => last.AddDays(7),
        UpdateFrequency.Monthly => last.AddMonths(1),
        _ => last.AddDays(1)
    };

    public async Task CheckAsync(bool manual, CancellationToken token)
    {
        if (Checking)
        {
            return;
        }

        if (!manual && !_enabled)
        {
            return;
        }

        var now = _clock.GetUtcNow();
        var due = _startup || _state.LastSuccessfulCheck is null || _state.LastSuccessfulCheck > now
            || NextCheck(_state.LastSuccessfulCheck.Value, _frequency) <= now;

        if (!manual && (!_enabled || !due || !_startup && _lastAttempt is not null && now - _lastAttempt < TimeSpan.FromMinutes(10)))
        {
            RefreshAvailable();
            return;
        }

        _active?.Dispose();
        _active = CancellationTokenSource.CreateLinkedTokenSource(token);
        var cancellation = _active.Token;
        _startup = false;
        _lastAttempt = now;
        Checking = true;
        Status = "Checking for updates...";

        try
        {
            var manifest = await _fetch(cancellation);
            cancellation.ThrowIfCancellationRequested();

            if (!manifest.IsValid())
            {
                throw new InvalidDataException("Unsupported update manifest.");
            }

            _state.Manifest = manifest;
            _state.LastSuccessfulCheck = _clock.GetUtcNow();
            await RefreshServiceAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
            RefreshAvailable(manual);
            Status = HasUpdates ? Summary + "." : "No newer versions found.";
            Raise(nameof(LastChecked));
            await PersistAsync(cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Status = "Update check cancelled.";
        }
        catch (Exception error) when (error is HttpRequestException or Client.Http.MonitoringApiException or IOException or System.Text.Json.JsonException or OperationCanceledException)
        {
            Status = "Could not check for updates. Try again later.";
        }
        finally
        {
            Checking = false;
        }
    }

    public async Task RefreshServiceAsync(CancellationToken token)
    {
        try
        {
            var value = await _serviceVersion(token);
            _installedService = SemanticVersion.TryParse(value, out _) ? value : null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error) when (error is HttpRequestException or Client.Http.MonitoringApiException or IOException or System.Text.Json.JsonException or OperationCanceledException)
        {
            _installedService = null;
        }

        Raise(nameof(ServiceVersion));
        RefreshAvailable();
    }

    public async Task DismissAsync(UpdateNotice notice, bool skip, CancellationToken token)
    {
        _state.Dismissals[notice.Component] = new UpdateDismissal(notice.AvailableVersion, skip,
            skip ? null : _clock.GetUtcNow().AddDays(1));
        RefreshAvailable();
        await PersistAsync(token);
    }

    public void Cancel() => _active?.Cancel();

    private void RefreshAvailable(bool manual = false)
    {
        var notices = new List<UpdateNotice>();
        var manifest = _state.Manifest;

        if (manifest is not null)
        {
            Add("desktop", "Desktop app", DesktopVersion, manifest.Desktop);
            Add("service", "Backend service", _installedService, manifest.Service);
        }

        if (!Available.SequenceEqual(notices))
        {
            Available.Clear();

            foreach (var notice in notices)
            {
                Available.Add(notice);
            }

            Raise(nameof(HasUpdates));
            Raise(nameof(Summary));
        }

        return;

        void Add(string component, string name, string? installed, string latest)
        {
            if (!SemanticVersion.TryParse(installed, out var current) || !SemanticVersion.TryParse(latest, out var remote)
                || remote.CompareTo(current) <= 0)
            {
                return;
            }

            var dismissal = _state.Dismissals.GetValueOrDefault(component);

            if (!manual && dismissal is not null && dismissal.Version == latest
                && (dismissal.Skipped || dismissal.RemindAfter > _clock.GetUtcNow()))
            {
                return;
            }

            notices.Add(new UpdateNotice(component, name, installed!, latest, manifest!.ReleaseUrl, manifest.ReleaseTag));
        }
    }

    private async Task PersistAsync(CancellationToken token)
    {
        try
        {
            await _store.SaveAsync(_state, token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Status += " Update preferences could not be saved.";
        }
    }
}
