using Avalonia.Controls;
using RTXLens.WarningUI;
using Avalonia.Threading;
using RTXLens.App.Platform.Tray;
using RTXLens.Contracts.Warnings;
using RTXLens.Presentation.Notifications;
using static RTXLens.App.Views.Shell.DesktopControls;

namespace RTXLens.App.Views.Shell;

internal sealed partial class ShellWindow
{
    private TrayService? _tray;
    private bool _exitFromTray;
    private bool _showingShutdown;

    private void InitializeWindowsIntegration(bool startMinimized)
    {
        if (!OperatingSystem.IsWindows() || TryGetPlatformHandle() is not { HandleDescriptor: "HWND" } handle)
        {
            return;
        }

        try
        {
            _tray = new TrayService(handle.Handle, action => Dispatcher.UIThread.Post(action), RestoreWindow, () =>
            {
                RestoreWindow();
                Navigate(Page.Settings);
            }, () =>
            {
                _exitFromTray = true;
                Close();
            }, _ => { });
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            System.Diagnostics.Debug.WriteLine(error);
        }

        Program.Instance?.ListenForActivation(() => Dispatcher.UIThread.Post(() =>
        {
            if (!_closed)
            {
                RestoreWindow();
            }
        }));

        Program.Instance?.ListenForWarnings(() => Dispatcher.UIThread.Post(() =>
        {
            if (_closed)
            {
                return;
            }

            RestoreWindow();
            ShowCurrentWarnings();
        }));

        _model.WarningRaised += OnWarningRaised;
        _model.Dashboard.PropertyChanged += OnLocalWarningStateChanged;
        _model.WarningShutdownAvailable += ShowWarningShutdown;

        if (startMinimized)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_tray?.IsAvailable == true)
                {
                    HideToTray();
                }
                else
                {
                    WindowState = WindowState.Minimized;
                }
            });
        }
    }

    private WarningPopup? _localPopup;
    private bool _simulationSoundLooping;

    private void OnLocalWarningStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (_simulationSoundLooping && args.PropertyName == nameof(_model.Dashboard.WarningSummary) &&
            (!_model.IsSimulation || !_model.Preferences.PlayWarningSound || !_model.Preferences.LoopWarningSound ||
             _model.Configuration.CurrentConfiguration?.Warnings.NotificationsEnabled != true || _model.Dashboard.CurrentWarnings.All(s => s.Phase != WarningPhase.Active)))
        {
            StopLocalWarningSound();
        }
    }

    private void StopLocalWarningSound()
    {
        _simulationSoundLooping = false;
        WarningSoundPlayer.Stop();
    }

    private void OnWarningRaised(WarningNotification notification)
    {
        var prefs = _model.Preferences;
        var loop = prefs.LoopWarningSound && _model.IsSimulation && notification.Key != "safeguard";

        if (prefs.PlayWarningSound && (!loop || !_simulationSoundLooping))
        {
            _simulationSoundLooping = false;
            try
            {
                WarningSoundPlayer.Play(prefs, _model.PreferencesDirectory, loop);
                _simulationSoundLooping = loop;
            }
            catch (IOException error) { System.Diagnostics.Debug.WriteLine(error); }
        }

        if (!prefs.WarningNotifications)
        {
            return;
        }

        if (_localPopup is null)
        {
            _localPopup = new WarningPopup(() =>
            {
                RestoreWindow();

                if (OperatingSystem.IsWindows() && _localPopup?.Tag is "safeguard")
                {
                    _model.OpenSafeguardHistory(this);
                }
                else
                {
                    ShowCurrentWarnings();
                }
            });

            _localPopup.Closed += (_, _) => { _localPopup = null; StopLocalWarningSound(); };
        }

        _localPopup.Tag = notification.Key;
        _localPopup.Update(notification.Title, notification.Message);

        if (!_localPopup.IsVisible)
        {
            _localPopup.Show();
        }
    }

    private void HideToTray()
    {
        CaptureWindowPlacement();
        Hide();

        _model.SetVisible(false);
    }

    private void RestoreWindow()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        _model.SetVisible(true);

        ShowWarningShutdown();
    }

    private async Task<bool> ConfirmExitAsync()
    {
        var suppress = new CheckBox { Content = "Do not show this again" };

        var details = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                DialogText("Closing RTX Lens ends any active performance recording."),
                DialogText("Monitoring, protection, and the background warning notifier continue running."),
                suppress
            }
        };

        if (!await ConfirmAsync(this, "Exit RTX Lens?", details, "Exit"))
        {
            return false;
        }

        return suppress.IsChecked != true || await _model.ChangePreferencesAsync(preferences => preferences with { SuppressExitConfirmation = true });
    }

    private async void ShowWarningShutdown()
    {
        if (_showingShutdown || _savingPlacement || !IsVisible || WindowState == WindowState.Minimized || _model.WarningShutdown is not { } reason)
        {
            return;
        }

        _showingShutdown = true;
        try
        {
            var detail = $"{reason.Warning}\nProfile: {reason.ProfileName}\n{reason.Detail}\n\n{reason.RequestedAt.ToLocalTime():g}";
            if (await ShowInformationAsync(this, "RTX Lens requested a shutdown", Caption(detail)))
            {
                await _model.AcknowledgeWarningShutdownAsync(reason);
            }
        }
        finally
        {
            _showingShutdown = false;
        }
    }
}
