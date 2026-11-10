using System.Collections.Specialized;
using System.ComponentModel;
using System.Net;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RTXLens.App.Controls;
using RTXLens.App.Services;
using RTXLens.Client.Http;
using RTXLens.Plugins.WireView.Contracts.Common;
using RTXLens.Presentation.Settings;
using RTXLens.Presentation.Telemetry;

namespace RTXLens.App.WireView.Windows;

internal sealed partial class WireViewDeviceWindow : Window
{
    private readonly AppRuntime _runtime;
    private HardwarePanelViewModel? _model;
    private bool _refreshing;
    private bool _applying;
    private bool _needsRefresh;
    private bool _showingSimulation;
    private DeviceConfig? _shownConfig;
    private WireViewSettingsEditor? _editor;
    private bool _queued;
    private bool _closed;
    internal FrameworkElement ThemeRoot => Root;

    internal WireViewDeviceWindow(AppRuntime runtime)
    {
        _runtime = runtime;
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "monitor.ico"));
        ApplyAppearance(runtime.Preferences);
        SetPanel(runtime.Dashboard.Panels.FirstOrDefault(panel => panel.IsWireView));
        Closed += OnClosed;
    }

    internal void ApplyAppearance(AppPreferences preferences) => ThemeService.Apply(Root, preferences);

    internal void SetPanel(HardwarePanelViewModel? model)
    {
        if (ReferenceEquals(model, _model))
        {
            if (_showingSimulation != _runtime.IsWireViewDeviceSimulated)
            {
                ApplyResult.IsOpen = false;
                RefreshRows();
            }

            return;
        }

        Unsubscribe();
        ApplyResult.IsOpen = false;
        _model = model;
        _shownConfig = null;
        _needsRefresh = false;
        if (_model is not null)
        {
            _model.PropertyChanged += OnModelChanged;
            _model.DeviceDetails.CollectionChanged += OnDetailsChanged;
        }
        RefreshRows();
    }

    internal async Task RefreshAsync()
    {
        if (_closed || _refreshing || _applying || _model is not { IsReporting: true })
        {
            return;
        }

        ApplyResult.IsOpen = false;
        _refreshing = true;
        UpdateStatus();
        try
        {
            await _runtime.ReadWireViewDeviceAsync();
        }
        finally
        {
            _refreshing = false;
            if (!_closed)
            {
                RefreshRows();
            }
        }
    }

    private void OnDetailsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (_queued || _closed)
        {
            return;
        }
        _queued = Root.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _queued = false;
            if (!_closed)
            {
                RefreshRows();
            }
        });
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(HardwarePanelViewModel.DeviceConfigurationStatus) or nameof(HardwarePanelViewModel.IsReporting)
            or nameof(HardwarePanelViewModel.DeviceText))
        {
            UpdateStatus();
        }
    }

    private void RefreshRows()
    {
        var configuration = _model?.DeviceConfiguration;
        var simulation = _runtime.IsWireViewDeviceSimulated;

        if (configuration is not null && ReferenceEquals(configuration, _shownConfig) && simulation == _showingSimulation)
        {
            UpdateStatus();
            return;
        }

        RowsHost.Children.Clear();
        _shownConfig = configuration;
        _showingSimulation = simulation;
        _editor = null;

        if (configuration is { EditToken: not null, Display: not null, FirmwareVersion: >= WireViewSettingsValidation.MinimumWritableFirmware }
            && _runtime.CanApplyWireViewDeviceSettings)
        {
            _editor = new WireViewSettingsEditor(configuration);
            _needsRefresh = false;
            RowsHost.Children.Add(_editor.Content);
        }
        else if (_model is not null)
        {
            var details = new StackPanel { Spacing = 8 };
            foreach (var metric in _model.DeviceDetails)
            {
                if (metric.Label == "Version" || (metric.Label == "Averaging" && configuration is { Version: 0 }))
                {
                    continue;
                }

                details.Children.Add(new MetricTile(metric, valueMaxWidth: 260, usePanelBorder: true));
            }

            if (details.Children.Count > 0)
            {
                RowsHost.Children.Add(new SectionPanel { Header = "Device configuration", Body = details });
            }
        }

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var canRefresh = !_refreshing && !_applying && _model is { IsReporting: true };
        FirmwareText.Text = _model?.DeviceConfiguration?.FirmwareVersion is { } firmware
            ? $"Firmware: v{firmware:00}"
            : "Firmware: unavailable";

        SettingsDescription.Text = _showingSimulation
            ? "Simulation mode. Apply saves changes for this session only."
            : _editor is null ? "Firmware settings are read-only until an editable configuration is detected."
                : "Apply saves your changes to the WireView.";

        RefreshButton.IsEnabled = canRefresh;
        ApplyButton.IsEnabled = canRefresh && !_needsRefresh && _editor is not null && _runtime.CanApplyWireViewDeviceSettings;
        if (_editor is { } editor)
        {
            editor.Content.IsHitTestVisible = canRefresh;
        }

        StatusText.Text = _model switch
        {
            null => "WireView is not available.",
            { IsReporting: false } => "WireView is not reporting. Refresh after reconnecting.",
            _ when _applying => "Applying WireView settings...",
            _ when _refreshing => "Reading WireView settings...",
            _ when !_needsRefresh && _shownConfig is not null && _editor is null =>
                _model.DeviceConfigurationStatus + " · Read only",
            _ => _model.DeviceConfigurationStatus
        };
    }

    private void Unsubscribe()
    {
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
            _model.DeviceDetails.CollectionChanged -= OnDetailsChanged;
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        Unsubscribe();
        RowsHost.Children.Clear();
        _model = null;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs args) => await RefreshAsync();

    private async void Apply_Click(object sender, RoutedEventArgs args)
    {
        if (!ApplyButton.IsEnabled || _model is not { } panel || _editor is not { } editor)
        {
            return;
        }

        var simulation = _showingSimulation;
        var device = panel.DeviceText;
        var writing = false;

        ApplyResult.IsOpen = false;
        _applying = true;
        UpdateStatus();

        try
        {
            var changes = editor.Read();
            if (changes == _shownConfig)
            {
                ShowApplyResult(InfoBarSeverity.Warning, "There are no changes to apply.");
                return;
            }

            var confirmed = await new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "Apply WireView settings?",
                Content = simulation
                    ? "These changes apply only to the simulated WireView."
                    : "Changes are saved to the device. Review any changes to its fan, fault limits, or shutdown actions before proceeding.",
                PrimaryButtonText = "Apply",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            }.ShowAsync();

            if (confirmed != ContentDialogResult.Primary || !IsCurrentTarget()
                || !ReferenceEquals(panel.DeviceConfiguration, _shownConfig) || !ReferenceEquals(_editor, editor))
            {
                return;
            }

            writing = true;
            var applied = await _runtime.ApplyWireViewDeviceSettingsAsync(panel, changes, simulation);
            if (applied && IsCurrentTarget())
            {
                ShowApplyResult(InfoBarSeverity.Success,
                    simulation ? "Saved to this simulation." : "Saved to WireView and verified.");
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or HttpRequestException
            or MonitoringApiException or IOException or OperationCanceledException or System.Text.Json.JsonException
            or System.Runtime.InteropServices.COMException)
        {
            if (IsCurrentTarget())
            {
                var invalid = error is ArgumentException or MonitoringApiException { StatusCode: HttpStatusCode.BadRequest };
                var stale = error is InvalidOperationException || error is MonitoringApiException { IsConflict: true };
                _needsRefresh = writing && !invalid;

                var severity = invalid || stale ? InfoBarSeverity.Warning : InfoBarSeverity.Error;
                ShowApplyResult(severity, error.Message);
            }
        }
        finally
        {
            _applying = false;
            if (!_closed)
            {
                RefreshRows();
            }
        }

        bool IsCurrentTarget() => !_closed && ReferenceEquals(_model, panel) && panel.DeviceText == device
            && simulation == _runtime.IsWireViewDeviceSimulated;
    }

    private void ShowApplyResult(InfoBarSeverity severity, string message)
    {
        ApplyResult.Severity = severity;
        ApplyResult.Message = message;
        ApplyResult.IsOpen = true;
    }
}
