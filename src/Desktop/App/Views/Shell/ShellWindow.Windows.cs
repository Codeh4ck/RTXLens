using Avalonia.Controls;
using Avalonia.Threading;
using RTXLens.App.Platform.Tray;
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

        _model.WarningRaised += OnWarningRaised;
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

    private void OnWarningRaised(WarningNotification notification) => _tray?.Notify(notification.Title, notification.Message);

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
                DialogText("Closing RTX Lens stops desktop notifications and ends any active performance recording."),
                DialogText("The backend service and its configured protection continue running."),
                suppress
            }
        };

        if (!await ConfirmAsync(this, "Exit RTX Lens?", details, "Exit"))
        {
            return false;
        }

        return suppress.IsChecked != true
            || await _model.ChangePreferencesAsync(preferences => preferences with { SuppressExitConfirmation = true });
    }

    private async void ShowWarningShutdown()
    {
        if (_showingShutdown || _savingPlacement || !IsVisible || WindowState == WindowState.Minimized
            || _model.WarningShutdown is not { } reason)
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
