using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Windowing;
using RTXLens.App.Platform;
using RTXLens.App.Services;
using RTXLens.App.Views;
using RTXLens.Presentation.Telemetry;
using RTXLens.Presentation.Settings;

namespace RTXLens.App;

public sealed partial class MainWindow : Window
{
    private readonly AppRuntime _runtime;
    private string _page;
    private string? _panelId;
    private bool _rendering = true;
    private string? _preferredPanelId;
    private string? _configurationSection;
    private bool _selectionQueued;
    private Task _closingCharts = Task.CompletedTask;
    public Presentation.Updates.UpdateViewModel Updates => _runtime.Updates;
    public DashboardViewModel Model { get; private set; }
    internal FrameworkElement ThemeRoot => Root;
    internal object? CurrentView => PageHost.Content;
    internal bool HardwareDetailsVisible => _rendering && _page == "hardware";
    internal bool ConfigurationVisible => _rendering && _page == "configuration";

    public Visibility WarningVisibility(bool hasWarning) => hasWarning ? Visibility.Visible : Visibility.Collapsed;

    internal void ApplyAppearance(AppPreferences preferences)
    {
        ThemeService.Apply(Root, preferences);

        PageHost.FontSize = 14 * preferences.TextScale;
        Tabs.FontSize = 14 * preferences.TextScale;
        AccentGlow.Visibility = preferences.ReducedEffects ? Visibility.Collapsed : Visibility.Visible;
    }

    internal void ShowPanel(string id)
    {
        _preferredPanelId = id;
        DisplayPanel(id);
    }

    private void DisplayPanel(string id)
    {
        Model.SelectedPanel = Model.ReportingPanels.First(panel => panel.Id == id);
        _panelId = id;
        _page = "hardware";
        RenderPage();
    }

    internal MainWindow(AppRuntime runtime)
    {
        _runtime = runtime;
        _preferredPanelId = runtime.Preferences.StartupTabId;
        _page = _preferredPanelId is "performance" or "event-log" ? _preferredPanelId : "hardware";

        Model = runtime.Dashboard;

        InitializeComponent();

        Title = DesktopBuild.WindowTitle;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = true;
            presenter.IsResizable = true;
        }

        runtime.OperationCompleted += message =>
        {
            OperationStatus.Text = message;
            ToolTipService.SetToolTip(OperationStatus, message);
        };

        if (runtime.IsDemo)
        {
            ModeLabel.Text = "DEMO · SIMULATED TELEMETRY";
        }

        Model.ReportingPanels.CollectionChanged += OnReportingPanelsChanged;

        SelectInitialPanel();

        if (_page is "performance" or "event-log")
        {
            RenderPage();
        }
    }

    private void OnReportingPanelsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
    {
        if (_selectionQueued)
        {
            return;
        }

        _selectionQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _selectionQueued = false;
            SelectInitialPanel();
        });
    }

    internal void RefreshRuntime(bool simulated)
    {
        Model.ReportingPanels.CollectionChanged -= OnReportingPanelsChanged;
        Model = _runtime.Dashboard;
        Model.ReportingPanels.CollectionChanged += OnReportingPanelsChanged;

        ModeLabel.Text = simulated ? "DEBUG SIMULATION ACTIVE" : _runtime.IsDemo ? "DEMO · SIMULATED TELEMETRY" : "POWER · PINS · PROTECTION";

        Bindings.Update();
        SelectInitialPanel();
        RenderPage();
    }

    private void SelectInitialPanel()
    {
        if (_page != "hardware")
        {
            return;
        }

        if (Model.ReportingPanels.Count > 0)
        {
            var selected = Model.ReportingPanels.FirstOrDefault(panel => panel.Id == _preferredPanelId)
                ?? Model.ReportingPanels.FirstOrDefault(panel => panel.Id == _panelId) ?? Model.ReportingPanels[0];

            if (_panelId != selected.Id || Model.SelectedPanel != selected)
            {
                DisplayPanel(selected.Id);
            }
        }
        else
        {
            Model.SelectedPanel = null;
            _panelId = null;

            RenderPage();
        }
    }

    private void HardwareTab_Click(object sender, RoutedEventArgs args)
    {
        var button = (ToggleButton)sender;
        ShowPanel((string)button.Tag);
        button.IsChecked = true;
    }

    private void Performance_Click(object sender, RoutedEventArgs args) => Navigate("performance");

    private void EventLog_Click(object sender, RoutedEventArgs args)
    {
        if (_page == "event-log")
        {
            EventLogTab.IsChecked = true;
            return;
        }

        Navigate("event-log");
    }

    private void Configuration_Click(object sender, RoutedEventArgs args) => Navigate("configuration");

    private void ApplicationHeader_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        var compact = args.NewSize.Width < 960;
        ApplicationHeader.RowSpacing = compact ? 6 : 0;
        Grid.SetRow(WebsiteLinks, compact ? 1 : 0);
        Grid.SetColumn(WebsiteLinks, compact ? 0 : 1);
        Grid.SetColumnSpan(WebsiteLinks, compact ? 3 : 1);
    }

    private async void ReleaseNotes_Click(object sender, RoutedEventArgs args) => await _runtime.OpenReleaseNotesAsync();

    private async void Support_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(new Uri("https://rtxlens.com/support")))
            {
                OperationStatus.Text = "The support page could not be opened.";
            }
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            OperationStatus.Text = "The support page could not be opened.";
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs args) => Navigate("settings");

    internal void Navigate(string page)
    {
        RememberConfigurationSection();

        _page = page;
        if (page != "hardware")
        {
            Model.SelectedPanel = null;
        }

        RenderPage();
    }

    internal void SetRendering(bool enabled)
    {
        if (_rendering == enabled)
        {
            return;
        }

        _rendering = enabled;
        if (enabled)
        {
            RenderPage();
        }
        else
        {
            RememberConfigurationSection();
            CloseCurrentChart();

            PageHost.Content = null;
        }
    }

    internal async Task CloseViewsAsync()
    {
        CloseCurrentChart();

        await _closingCharts;

        PageHost.Content = null;
    }

    private void CloseCurrentChart()
    {
        if (PageHost.Content is PerformanceChartView chart)
        {
            var closing = chart.CloseAsync();
            _closingCharts = _closingCharts.IsCompletedSuccessfully ? closing : Task.WhenAll(_closingCharts, closing);
        }
    }

    private void RememberConfigurationSection()
    {
        if (PageHost.Content is ConfigurationView view)
        {
            _configurationSection = view.SelectedSection;
        }
    }

    private void RenderPage()
    {
        if (!_rendering)
        {
            return;
        }

        CloseCurrentChart();
        _runtime.RefreshPresentation();

        PerformanceTab.IsChecked = _page == "performance";
        EventLogTab.IsChecked = _page == "event-log";

        PageHost.Content = _page switch
        {
            "performance" => new PerformanceChartView(_runtime),
            "event-log" => new EventLogView(_runtime),
            "settings" => new SettingsView(_runtime),
            "configuration" => new ConfigurationView(_runtime),
            _ when Model.ReportingPanels.FirstOrDefault(panel => panel.Id == _panelId) is { IsMsi: true } msi => new MsiHardwareView(msi, _runtime),
            _ when Model.ReportingPanels.FirstOrDefault(panel => panel.Id == _panelId) is { } panel => new HardwareView(panel, _runtime),
            _ => new TextBlock { Text = "No plugins are reporting. Open Configuration to check device and plugin settings.", TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(24) }
        };

        if (PageHost.Content is ConfigurationView configuration && _configurationSection is { } section)
        {
            configuration.TrySelectSection(section);
        }
    }
}
