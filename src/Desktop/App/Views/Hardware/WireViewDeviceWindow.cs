using System.ComponentModel;
using System.Net;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using RTXLens.App.ViewModels.Shell;
using RTXLens.App.Views.Shell;
using RTXLens.Client.Http;
using RTXLens.Plugins.WireView.Contracts.Common;
using RTXLens.Presentation.Telemetry;
using static RTXLens.App.Views.Shell.DesktopControls;

namespace RTXLens.App.Views.Hardware;

internal sealed class WireViewDeviceWindow : LensWindow
{
    private readonly ShellViewModel _shell;
    private readonly HardwarePanelViewModel _panel;
    private readonly IMonitoringApi _api;
    private readonly string _device;
    private readonly bool _simulated;
    private readonly CancellationTokenSource _lifetime;
    private readonly TextBlock _firmware = Caption();
    private readonly TextBlock _description = Caption();
    private readonly ContentControl _alert = Alert();
    private readonly ContentControl _editorHost = new();
    private readonly Button _refresh;
    private readonly Button _apply;
    private WireViewSettingsEditor? _editor;
    private DeviceConfig? _loaded;
    private bool _busy;
    private bool _closed;

    public WireViewDeviceWindow(ShellViewModel shell, HardwarePanelViewModel panel)
    {
        _shell = shell;
        _panel = panel;
        _api = shell.Api;
        _device = panel.DeviceText;
        _simulated = shell.IsSimulation;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(shell.StopToken);

        Title = "RTX Lens - WireView Pro II device settings";
        Width = 830;
        Height = 780;
        MinWidth = 620;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _refresh = ActionButton("Refresh", "refresh", async void () => await RefreshAsync());
        _apply = CommitButton("Apply", async void () => await ApplyAsync());
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _refresh, _apply } };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        header.Children.Add(new StackPanel { Spacing = 4, Children = { _firmware, _description } });
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);

        var scroll = Scroll(_editorHost);
        _editorHost.Margin = new Thickness(0, 0, PageScrollbarGutter, 0);
        var layout = new Grid { Margin = new Thickness(20), RowDefinitions = new RowDefinitions("Auto,Auto,*"), RowSpacing = 12 };
        layout.Children.Add(header);
        Grid.SetRow(_alert, 1);
        layout.Children.Add(_alert);
        Grid.SetRow(scroll, 2);
        layout.Children.Add(scroll);
        Body = layout;

        Opened += async (_, _) => await RefreshAsync();
        _panel.PropertyChanged += PanelChanged;
        _shell.PropertyChanged += ShellChanged;
        Closed += (_, _) =>
        {
            _closed = true;
            _panel.PropertyChanged -= PanelChanged;
            _shell.PropertyChanged -= ShellChanged;
            _lifetime.Cancel();
            _lifetime.Dispose();
        };

        UpdateActions();
    }

    private bool IsCurrent => !_closed && ReferenceEquals(_shell.Api, _api)
        && _shell.CanOpenWireViewSettings && _shell.Dashboard.Panels.Contains(_panel) && _panel.IsReporting && _panel.DeviceText == _device;

    private void PanelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(HardwarePanelViewModel.IsReporting) or nameof(HardwarePanelViewModel.DeviceText))
        {
            if (!IsCurrent)
            {
                Invalidate();
            }

            UpdateActions();
        }
    }

    private void ShellChanged(object? sender, PropertyChangedEventArgs args)
    {
        if ((args.PropertyName is nameof(ShellViewModel.Api) or nameof(ShellViewModel.CanOpenWireViewSettings)) && !IsCurrent)
        {
            Invalidate();
            UpdateActions();
        }
    }

    private async Task RefreshAsync()
    {
        if (_busy || !IsCurrent)
        {
            return;
        }

        _busy = true;
        _loaded = null;
        UpdateActions();
        SetAlert(_alert, "Reading WireView Pro II settings...");
        try
        {
            var config = await _api.GetWireViewConfigurationAsync(_lifetime.Token);
            if (IsCurrent)
            {
                ShowConfiguration(config);
                SetAlert(_alert, null);
            }
        }
        catch (Exception error) when (IsRequestError(error))
        {
            if (IsCurrent)
            {
                SetAlert(_alert, error.Message, "error");
            }
        }
        finally
        {
            _busy = false;
            UpdateActions();
        }
    }

    private async Task ApplyAsync()
    {
        if (_busy || !IsCurrent || _loaded is not { } original || _editor is not { } editor)
        {
            return;
        }

        _busy = true;
        UpdateActions();
        var writing = false;
        try
        {
            var changes = editor.Read();
            if (changes == original)
            {
                SetAlert(_alert, "There are no changes to apply.", "info");
                return;
            }

            var confirmed = await ConfirmAsync(this, "Apply WireView Pro II settings?",
                _simulated ? "These changes only affect this simulation."
                    : "These settings will be written to the device. Review any changes to fan settings, fault thresholds, or shutdown actions.",
                "Apply");
            if (!confirmed || !IsCurrent || !ReferenceEquals(original, _loaded))
            {
                return;
            }

            writing = true;
            SetAlert(_alert, "Writing and verifying settings...");
            var verified = await _api.ApplyWireViewConfigurationAsync(changes, _lifetime.Token);
            if (IsCurrent)
            {
                ShowConfiguration(verified);
                SetAlert(_alert, _simulated ? "Saved to this simulation." : "Saved to WireView Pro II and verified.", "success");
            }
        }
        catch (Exception error) when (IsRequestError(error))
        {
            if (IsCurrent)
            {
                var invalid = error is ArgumentException or MonitoringApiException { StatusCode: HttpStatusCode.BadRequest };
                if (writing && !invalid)
                {
                    _loaded = null;
                }

                SetAlert(_alert, error.Message, invalid ? "warning" : "error");
            }
        }
        finally
        {
            _busy = false;
            UpdateActions();
        }
    }

    private void ShowConfiguration(DeviceConfig config)
    {
        _panel.SetDeviceConfiguration(config);
        _loaded = config;
        _editor = null;
        _firmware.Text = config.FirmwareVersion is { } version ? $"Firmware v{version:00}" : "Firmware version unavailable";

        if (config is { EditToken: not null, Display: not null, FirmwareVersion: >= WireViewSettingsValidation.MinimumWritableFirmware })
        {
            _editor = new WireViewSettingsEditor(config);
            _editorHost.Content = _editor.Content;
            _description.Text = _simulated ? "Simulation mode. Changes last until simulation ends."
                : "Apply saves these settings to the device.";
            return;
        }

        var rows = new StackPanel { Spacing = 8 };
        foreach (var reading in _panel.DeviceDetails.Where(item => item.Label != "Version"
                     && (item.Label != "Averaging" || WireViewSettingsValidation.AveragingOptionCount(config.Version, config.FirmwareVersion) > 0)))
        {
            rows.Children.Add(HardwarePage.Readout(reading));
        }

        _editorHost.Content = Section("Device configuration", rows);
        _description.Text = "This firmware configuration is read-only.";
    }

    private void Invalidate()
    {
        _loaded = null;
        _description.Text = "Device or connection changed. Reopen device settings if the device was replaced.";
    }

    private void UpdateActions()
    {
        _refresh.IsEnabled = !_busy && IsCurrent;
        _apply.IsEnabled = _refresh.IsEnabled && _loaded is not null && _editor is not null;
        _editorHost.IsEnabled = _refresh.IsEnabled && _loaded is not null;
    }

    private static bool IsRequestError(Exception error) => error is ArgumentException or InvalidOperationException
        or HttpRequestException or MonitoringApiException or IOException or OperationCanceledException or JsonException;
}
