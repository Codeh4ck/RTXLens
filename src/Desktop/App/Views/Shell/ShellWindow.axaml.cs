using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
#if TELEMETRY_DEBUG
using Avalonia.Input;
#endif
using Avalonia.Interactivity;
using Avalonia.Threading;
using RTXLens.App.ViewModels.Shell;
using RTXLens.App.Views.Configuration;
using RTXLens.App.Views.Diagnostics;
using RTXLens.App.Views.Hardware;
using RTXLens.App.Views.Performance;
using RTXLens.App.Views.Settings;
using RTXLens.Presentation.Settings;
using RTXLens.Presentation.Telemetry;

namespace RTXLens.App.Views.Shell;

internal sealed partial class ShellWindow : LensWindow
{
    private readonly ShellViewModel _model;
    private Page _page;
    private string? _preferredPanelId;
    private bool _selectionQueued;
    private bool _closed;
    private bool _savingPlacement;
    private bool _placementSaved;
    private Size _normalSize;
    private PixelPoint _normalPosition;

    public ShellWindow(ShellViewModel model, bool startMinimized = false)
    {
        _model = model;
        _preferredPanelId = model.Preferences.StartupTabId;
        InitializeComponent();

        DataContext = model;
#if TELEMETRY_DEBUG
        AddHandler(KeyDownEvent, OnDebugShortcut, RoutingStrategies.Tunnel);
#endif
        Width = model.Preferences.RememberWindowSize ? model.Preferences.WindowWidth : AppPreferences.DefaultWindowWidth;
        Height = model.Preferences.RememberWindowSize ? model.Preferences.WindowHeight : AppPreferences.DefaultWindowHeight;

        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var saved = model.Preferences.WindowPlacements?.GetValueOrDefault("main");
        if (model.Preferences.RememberWindowPosition && saved is { X: { } x, Y: { } y })
        {
            var point = new PixelPoint(x, y);
            if (Screens.All.Any(screen => screen.WorkingArea.Contains(point)))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = point;
            }
        }

        Header.SizeChanged += (_, args) =>
        {
            var compact = args.NewSize.Width < 960;
            Header.RowSpacing = compact ? 6 : 0;
            Grid.SetRow(WebsiteLinks, compact ? 1 : 0);
            Grid.SetColumn(WebsiteLinks, compact ? 0 : 1);
            Grid.SetColumnSpan(WebsiteLinks, compact ? 3 : 1);
        };

        model.Dashboard.ReportingPanels.CollectionChanged += OnReportingPanelsChanged;
        model.PropertyChanged += OnShellChanged;
        Opened += (_, _) =>
        {
            ClampWindowToScreen();
            CaptureWindowPlacement();
            InitializeWindowsIntegration(startMinimized || model.Preferences.StartMinimized);
        };
        Closing += OnClosing;
        Closed += OnClosed;
        PositionChanged += (_, _) => CaptureWindowPlacement();
        Resized += (_, _) => CaptureWindowPlacement();
        PropertyChanged += (_, args) =>
        {
            if (args.Property == WindowStateProperty)
            {
                _model.SetVisible(IsVisible && WindowState != WindowState.Minimized);
                if (WindowState == WindowState.Minimized && _model.Preferences.MinimizeToTray && _tray?.IsAvailable == true)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (WindowState == WindowState.Minimized && !_closed)
                        {
                            HideToTray();
                        }
                    });
                }
            }
        };

        switch (_preferredPanelId)
        {
            case "performance":
                Navigate(Page.Performance);
                break;
            case "event-log":
                Navigate(Page.EventLog);
                break;
            default:
                RefreshHardwarePage();
                break;
        }
    }

#if TELEMETRY_DEBUG
    private void OnDebugShortcut(object? sender, KeyEventArgs args)
    {
        const KeyModifiers modifiers = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift;
        if (args is { Key: Key.F11, KeyModifiers: modifiers })
        {
            _model.RevealDebugConsole();
            args.Handled = true;
        }
    }
#endif

    private void ClampWindowToScreen()
    {
        if (Screens.ScreenFromWindow(this) is not { } screen)
        {
            return;
        }

        var area = screen.WorkingArea;
        Width = Math.Max(MinWidth, Math.Min(Width, area.Width / screen.Scaling));
        Height = Math.Max(MinHeight, Math.Min(Height, area.Height / screen.Scaling));

        var maxX = Math.Max(area.X, area.Right - (int)Math.Ceiling(Width * screen.Scaling));
        var maxY = Math.Max(area.Y, area.Bottom - (int)Math.Ceiling(Height * screen.Scaling));
        Position = new PixelPoint(Math.Clamp(Position.X, area.X, maxX), Math.Clamp(Position.Y, area.Y, maxY));
    }

    private void CaptureWindowPlacement()
    {
        if (WindowState == WindowState.Normal)
        {
            _normalSize = ClientSize;
            _normalPosition = Position;
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_placementSaved || args.CloseReason != WindowCloseReason.WindowClosing)
        {
            return;
        }

        args.Cancel = true;
        if (_savingPlacement)
        {
            return;
        }

        if (!_exitFromTray && _model.Preferences.CloseToTray && _tray?.IsAvailable == true)
        {
            HideToTray();
            return;
        }

        _savingPlacement = true;
        try
        {
            if (!_model.Preferences.SuppressExitConfirmation)
            {
                RestoreWindow();
                if (!await ConfirmExitAsync())
                {
                    return;
                }
            }

            CaptureWindowPlacement();
            await _model.SaveMainPlacementAsync(_normalSize, _normalPosition);
            _placementSaved = true;
            Close();
        }
        finally
        {
            _savingPlacement = false;
            _exitFromTray = false;
        }
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        _closed = true;
        _model.Dashboard.ReportingPanels.CollectionChanged -= OnReportingPanelsChanged;
        _model.PropertyChanged -= OnShellChanged;
        PageHost.Content = null;
        _model.WarningRaised -= OnWarningRaised;
        _model.WarningShutdownAvailable -= ShowWarningShutdown;
        _tray?.Dispose();
        _model.Dispose();
    }

    private void OnReportingPanelsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (_selectionQueued)
        {
            return;
        }

        _selectionQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _selectionQueued = false;

            if (!_closed && _page == Page.Hardware)
            {
                RefreshHardwarePage();
            }
        });
    }

    private void OpenHardware(object? sender, RoutedEventArgs args)
    {
        if (sender is Control { DataContext: HardwarePanelViewModel panel })
        {
            _preferredPanelId = panel.Id;
            Navigate(Page.Hardware, panel);
        }
    }

    private void OpenPerformance(object? sender, RoutedEventArgs args) => Navigate(Page.Performance);

    private void OpenEvents(object? sender, RoutedEventArgs args) => Navigate(Page.EventLog);

    private void OpenConfiguration(object? sender, RoutedEventArgs args) => Navigate(Page.Configuration);

    private void OpenSettings(object? sender, RoutedEventArgs args) => Navigate(Page.Settings);

    private async void OpenSupport(object? sender, RoutedEventArgs args)
    {
        await _model.RunAsync(() => Launcher.LaunchUriAsync(new Uri("https://rtxlens.com/support")));
    }

    private async void OpenReleaseNotes(object? sender, RoutedEventArgs args)
    {
        if (sender is not HyperlinkButton link)
        {
            return;
        }

        var label = link.Content;
        link.Content = "Loading release notes…";
        link.IsEnabled = false;
        try
        {
            await ReleaseNotesWindow.ShowAsync(this, _model.StopToken);
        }
        finally
        {
            link.Content = label;
            link.IsEnabled = true;
        }
    }

    private void RefreshHardwarePage()
    {
        var dashboard = _model.Dashboard;
        var selected = dashboard.ReportingPanels.FirstOrDefault(panel => panel.Id == _preferredPanelId)
            ?? dashboard.SelectedPanel
            ?? dashboard.ReportingPanels.FirstOrDefault();

        if (PageHost.Content is null || selected != dashboard.SelectedPanel || selected is null && PageHost.Content is HardwarePage)
        {
            Navigate(Page.Hardware, selected);
        }
    }

    private void OnShellChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ShellViewModel.Configuration) && _page == Page.Configuration)
        {
            Navigate(Page.Configuration);
        }
    }

    private void Navigate(Page page, HardwarePanelViewModel? panel = null)
    {
        _page = page;
        _model.ShowHardware = page == Page.Hardware;
        _model.ShowConfiguration = page == Page.Configuration;
        _model.Dashboard.SelectedPanel = panel;
        PerformanceTab.IsChecked = page == Page.Performance;
        EventLogTab.IsChecked = page == Page.EventLog;
        _model.RefreshPresentation(force: true);

        PageHost.Content = page switch
        {
            Page.Configuration => new ConfigurationPage(_model),
            Page.Settings => new SettingsPage(_model),
            Page.Performance => new PerformancePage(_model.Recorder),
            Page.EventLog => new EventLogPage(_model.Logs, _model.StopToken, _model.LocalEvents),
            _ when panel is not null => new HardwarePage(panel, _model),
            _ => new TextBlock
            {
                Text = "No plugins are reporting. Open Configuration to check device and plugin settings.",
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            }
        };
    }

    private enum Page
    {
        Hardware,
        Performance,
        EventLog,
        Configuration,
        Settings
    }
}
