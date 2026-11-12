using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using RTXLens.App.Views.Safeguard;
using RTXLens.Contracts.Sensors;
using RTXLens.Plugins.Msi.Contracts.Safeguard;
using RTXLens.Presentation.Notifications;

namespace RTXLens.App.ViewModels.Shell;

internal sealed partial class ShellViewModel
{
    private SafeguardStorage? _safeguardStorage;
    private Guid _safeguardConsumer;
    private readonly Dictionary<Guid, DateTimeOffset> _seenSafeguard = [];
    private SafeguardExecutionNotice[] _safeguardNotices = [];
    private int _safeguardAvailabilityRevision;

    [ObservableProperty]
    public partial bool HasSafeguardHistory { get; private set; }

    public event Action? SafeguardHistoryChanged;

    public void SetSafeguardHistoryAvailability(bool available)
    {
        _safeguardAvailabilityRevision++;
        HasSafeguardHistory = available;
    }

    [SupportedOSPlatform("windows")]
    private async Task LoadSafeguardHistoryAsync()
    {
        var revision = _safeguardAvailabilityRevision;
        try
        {
            var available = await Task.Run(() =>
            {
                if (!Directory.Exists(Path.Combine(SafeguardStorage.DefaultRoot, "history")))
                {
                    return false;
                }

                var storage = new SafeguardStorage();
                var cutoff = DateTimeOffset.UtcNow.AddDays(-storage.RetentionDays);
                return storage.Ids("history").Any(id => storage.Read<SafeguardExecution>("history", id)?.StartedAt >= cutoff);
            });

            if (!_disposed && revision == _safeguardAvailabilityRevision)
            {
                HasSafeguardHistory = available;
            }
        }
        catch (Exception error) when (SafeguardScriptWindow.IsConfigurationError(error))
        {
            System.Diagnostics.Debug.WriteLine(error);
        }
    }

    [SupportedOSPlatform("windows")]
    public void OpenSafeguardHistory(Window owner)
    {
        if (!HasSafeguardHistory || IsSimulation)
        {
            return;
        }

        _ = RunAsync(() =>
        {
            var open = owner.OwnedWindows.OfType<SafeguardHistoryWindow>().FirstOrDefault();
            if (open is null)
            {
                var window = new SafeguardHistoryWindow(this);
                TrackToolWindow(window, "safeguard-history", owner);
                window.Show(owner);
            }
            else
            {
                open.Activate();
            }

            return Task.CompletedTask;
        });
    }

    [SupportedOSPlatform("windows")]
    private void ReceiveSafeguard(SafeguardExecutionNotice[] notices)
    {
        if (_safeguardNotices.SequenceEqual(notices))
        {
            return;
        }

        _safeguardNotices = notices;
        SetSafeguardHistoryAvailability(notices.Length > 0);
        SafeguardHistoryChanged?.Invoke();
        if (notices.Length == 0)
        {
            return;
        }

        try
        {
            if (_safeguardStorage is null)
            {
                using var identity = WindowsIdentity.GetCurrent();
                if (identity.User is { } user)
                {
                    _safeguardConsumer = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(user.Value)).AsSpan(0, 16));
                }

                var storage = new SafeguardStorage();
                if (_safeguardConsumer != Guid.Empty)
                {
                    foreach (var entry in storage.Read<Dictionary<Guid, DateTimeOffset>>("seen", _safeguardConsumer) ?? [])
                    {
                        _seenSafeguard.TryAdd(entry.Key, entry.Value);
                    }
                }

                _safeguardStorage = storage;
            }
        }
        catch (Exception error) when (SafeguardScriptWindow.IsConfigurationError(error))
        {
            System.Diagnostics.Debug.WriteLine(error);
        }

        var pending = notices.Where(pending => _seenSafeguard.TryAdd(pending.Id, pending.StartedAt)).ToArray();
        if (pending.Length == 0)
        {
            return;
        }

        var message = pending is [var notice] ? $"{notice.Script} · Connector {notice.Connector} · {notice.Status}"
            : $"{pending.Length} new events. Open Safeguard+ History for details.";

        WarningRaised?.Invoke(new WarningNotification("safeguard", "Safeguard+ script executed", message));

        var oldest = DateTimeOffset.UtcNow.AddDays(-365);
        foreach (var entry in _seenSafeguard.Where(entry => entry.Value < oldest).ToArray())
        {
            _seenSafeguard.Remove(entry.Key);
        }

        try
        {
            if (_safeguardConsumer != Guid.Empty)
            {
                _safeguardStorage?.Write("seen", _safeguardConsumer, _seenSafeguard);
            }
        }
        catch (Exception error) when (SafeguardScriptWindow.IsConfigurationError(error))
        {
            System.Diagnostics.Debug.WriteLine(error);
        }
    }
}
