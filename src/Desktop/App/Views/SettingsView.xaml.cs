using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RTXLens.App.Services;
using RTXLens.Presentation.Settings;

namespace RTXLens.App.Views;

public sealed partial class SettingsView : UserControl
{
    private readonly AppRuntime _runtime;
    public AppSettingsViewModel Model { get; }
    public Presentation.Updates.UpdateViewModel Updates => _runtime.Updates;

    internal SettingsView(AppRuntime runtime)
    {
        _runtime = runtime;
        Model = runtime.Settings;
        InitializeComponent();
        ThemePicker.SelectedIndex = Model.Theme switch { AppTheme.Dark => 1, AppTheme.Light => 2, _ => 0 };
        StartupToggle.IsEnabled = runtime.CanRegisterStartup;
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (ThemePicker.SelectedIndex >= 0)
        {
            Model.Theme = ThemePicker.SelectedIndex switch { 1 => AppTheme.Dark, 2 => AppTheme.Light, _ => AppTheme.System };
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs args) => await Updates.CheckAsync(true, _runtime.StopToken);

    private async void ViewRelease_Click(object sender, RoutedEventArgs args)
    {
        var notice = FindUpdate(sender);

        if (notice is null)
        {
            return;
        }

        try
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(new Uri(notice.ReleaseUrl)))
            {
                Model.Message = "The release page could not be opened in your browser.";
            }
        }
        catch (Exception error)
        {
            Model.Message = "The release page could not be opened: " + error.Message;
        }
    }

    private async void ViewReleaseNotes_Click(object sender, RoutedEventArgs args)
    {
        if (FindUpdate(sender) is { } notice)
        {
            await _runtime.OpenReleaseNotesAsync(notice);
        }
    }

    private async void RemindUpdate_Click(object sender, RoutedEventArgs args) => await DismissUpdateAsync(sender, false);

    private async void SkipUpdate_Click(object sender, RoutedEventArgs args) => await DismissUpdateAsync(sender, true);

    private Presentation.Updates.UpdateNotice? FindUpdate(object sender) =>
        Updates.Available.FirstOrDefault(notice => notice.Component == ((sender as FrameworkElement)?.Tag as string));

    private async Task DismissUpdateAsync(object sender, bool skip)
    {
        var notice = FindUpdate(sender);

        if (notice is null)
        {
            return;
        }

        try
        {
            await Updates.DismissAsync(notice, skip, _runtime.StopToken);
        }
        catch (OperationCanceledException) when (_runtime.StopToken.IsCancellationRequested)
        {
        }
    }


    private void Reconnect_Click(object sender, RoutedEventArgs args) => _runtime.Reconnect();

    private void RememberWindowSize_Toggled(object sender, RoutedEventArgs args)
    {
        if (IsLoaded && ((ToggleSwitch)sender).IsOn != Model.RememberWindowSize)
        {
            Model.RememberWindowSize = ((ToggleSwitch)sender).IsOn;
        }
    }

    private void RememberWindowPosition_Toggled(object sender, RoutedEventArgs args)
    {
        if (IsLoaded && ((ToggleSwitch)sender).IsOn != Model.RememberWindowPosition)
        {
            Model.RememberWindowPosition = ((ToggleSwitch)sender).IsOn;
        }
    }
}
