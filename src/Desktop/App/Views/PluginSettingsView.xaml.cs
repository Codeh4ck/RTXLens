using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RTXLens.App.Services;
using RTXLens.Presentation.Configuration;
using RTXLens.Presentation.Settings;

namespace RTXLens.App.Views;

public sealed partial class PluginSettingsView : UserControl
{
    private readonly AppRuntime _runtime;
    private bool _afterburnerDetected;
    public PluginSettingsEditorViewModel Model { get; }
    public AppSettingsViewModel Preferences { get; }

    internal PluginSettingsView(PluginSettingsEditorViewModel model, AppRuntime runtime)
    {
        Model = model;
        _runtime = runtime;
        Preferences = runtime.Settings;
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += (_, _) => Model.PropertyChanged -= OnPluginChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (!Model.IsMsi)
        {
            return;
        }

        Model.PropertyChanged -= OnPluginChanged;
        Model.PropertyChanged += OnPluginChanged;
        await RefreshAfterburnerAvailabilityAsync();
    }

    internal async Task RefreshAfterburnerAvailabilityAsync()
    {
        _afterburnerDetected = await AppRuntime.IsAfterburnerInstalledAsync();
        UpdateAfterburnerButton();
    }

    private void OnPluginChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(Model.Enabled))
        {
            UpdateAfterburnerButton();
        }
    }

    private void UpdateAfterburnerButton()
    {
        if (AfterburnerIntegrationButton is { } button)
        {
            button.IsEnabled = Model is { IsMsi: true, Enabled: true } && _afterburnerDetected;
            ToolTipService.SetToolTip(button, !Model.Enabled ? "Enable the MSI plugin first."
                : !_afterburnerDetected ? "Afterburner was not detected. Install or start it, then reopen MSI configuration."
                : "Configure Afterburner Safeguard+ in a separate window.");
        }
    }

    private async void Afterburner_Click(object sender, RoutedEventArgs args)
    {
        await RefreshAfterburnerAvailabilityAsync();
        if (Model.Enabled && _afterburnerDetected)
        {
            await _runtime.OpenAfterburnerSettingsAsync();
        }
    }

    private async void FanCurve_Click(object sender, RoutedEventArgs args) => await _runtime.OpenMsiFanCurveAsync();

    private async void RefreshDevices_Click(object sender, RoutedEventArgs args) => await _runtime.RefreshDevicesAsync();
    private async void RefreshNvidiaDevices_Click(object sender, RoutedEventArgs args) => await _runtime.RefreshNvidiaDevicesAsync();

    private async void RefreshMsiDevices_Click(object sender, RoutedEventArgs args) => await _runtime.RefreshMsiDevicesAsync();
}
