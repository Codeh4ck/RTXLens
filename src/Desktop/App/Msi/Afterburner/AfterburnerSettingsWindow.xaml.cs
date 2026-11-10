using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RTXLens.App.Services;
using RTXLens.Presentation.Settings;

namespace RTXLens.App.Msi.Afterburner;

internal sealed partial class AfterburnerSettingsWindow : Window
{
    private bool _closeApproved;
    private bool _confirmingClose;
    internal AfterburnerSettingsView Integration { get; }
    internal FrameworkElement ThemeRoot => Root;

    internal AfterburnerSettingsWindow(AppRuntime runtime)
    {
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "monitor.ico"));
        Integration = new AfterburnerSettingsView(runtime, AppWindow.Id);
        Integration.CloseRequested += Close;
        IntegrationHost.Content = Integration;
        ApplyAppearance(runtime.Preferences);
        AppWindow.Closing += OnClosing;
        Closed += (_, _) =>
        {
            Integration.StopMonitoring();
#if DEBUG || TELEMETRY_DEBUG
            AfterburnerDiffWindow.CurrentPreview?.CancelPreview();
#endif
        };
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeApproved)
        {
            return;
        }

        Integration.CommitPendingEdits();
        if (Integration is { IsBusy: false, HasPendingChanges: false } && !_confirmingClose)
        {
            return;
        }

        args.Cancel = true;
        if (await ConfirmDiscardAsync())
        {
            CloseAfterConfirmation();
        }
    }

    internal async Task<bool> ConfirmDiscardAsync()
    {
        if (_confirmingClose || Integration.IsBusy)
        {
            return false;
        }

        Integration.CommitPendingEdits();
        if (!Integration.HasPendingChanges)
        {
            return true;
        }

        _confirmingClose = true;
        try
        {
            Activate();
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                RequestedTheme = Root.ActualTheme,
                Title = "Unapplied changes",
                Content = "There are pending changes that have not been applied. Go back to keep editing, or exit and discard these changes?",
                PrimaryButtonText = "Exit without applying",
                CloseButtonText = "Go back",
                DefaultButton = ContentDialogButton.Close
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            _confirmingClose = false;
        }
    }

    internal void CloseAfterConfirmation()
    {
        _closeApproved = true;
        Close();
    }

    internal void ApplyAppearance(AppPreferences preferences) => ThemeService.Apply(Root, preferences);
}