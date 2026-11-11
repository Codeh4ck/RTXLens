using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RTXLens.App.Controls;
using RTXLens.Presentation.Telemetry;

namespace RTXLens.App.Views;

public sealed partial class GpuStatusView : UserControl
{
    private const double MinimumThreeColumnWidth = 1240;
    private const double PanelSpacing = 12;
    private const double ColumnValueMaxWidth = 150;
    private const double VramValueMaxWidth = 240;

    public GpuStatusViewModel Model { get; }
    public bool ShowSummary { get; }

    private readonly IReadOnlyList<MetricViewModel> _additionalMetrics;
    private bool _stacked;

    internal GpuStatusView(GpuStatusViewModel model, bool showSummary, IReadOnlyList<MetricViewModel> additionalMetrics)
    {
        Model = model;
        ShowSummary = showSummary;
        _additionalMetrics = additionalMetrics;
        InitializeComponent();

        if (showSummary)
        {
            InitializeStandaloneLayout();
        }

        RebuildMetrics(null, null);
        Loaded += (_, _) =>
        {
            if (ShowSummary)
            {
                Model.DetailedMetrics.CollectionChanged += RebuildMetrics;
                RebuildMetrics(null, null);
            }
        };
        Unloaded += (_, _) => Model.DetailedMetrics.CollectionChanged -= RebuildMetrics;
    }

    private void InitializeStandaloneLayout()
    {
        StatusFrame.Style = (Style)Application.Current.Resources["MonitorCard"];

        StatusColumns.ColumnSpacing = PanelSpacing;
        for (var index = 0; index < 3; index++)
        {
            StatusColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            StatusColumns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        // Materialize the standalone panels before accessing their hosts.
        FindName(nameof(PowerStatusFrame));
        FindName(nameof(PcieStatusFrame));

        var boostLimitInfo = (Button)ActiveBoostLimitHost.Content;
        ActiveBoostLimitHost.Content = null;
        ActiveBoostLimitHost.Content = new MetricTile(Model.Diagnostics.ActiveBoostLimits, valueMaxWidth: ColumnValueMaxWidth,
            usePanelBorder: true, valueAction: boostLimitInfo);
        AddMetrics(PowerMetricsHost, Model.Diagnostics.PowerAndPerformance, ColumnValueMaxWidth);
        AddMetrics(PcieMetricsHost, Model.Diagnostics.Pcie, valueMaxWidth: 240);
        StatusColumns.SizeChanged += OnStatusColumnsSizeChanged;
    }

    private void OnStatusColumnsSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var stacked = args.NewSize.Width < MinimumThreeColumnWidth;
        if (_stacked == stacked)
        {
            return;
        }

        _stacked = stacked;
        var sideColumnWidth = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        StatusColumns.ColumnDefinitions[1].Width = sideColumnWidth;
        StatusColumns.ColumnDefinitions[2].Width = sideColumnWidth;
        StatusColumns.ColumnSpacing = stacked ? 0 : PanelSpacing;
        StatusColumns.RowSpacing = stacked ? PanelSpacing : 0;

        Grid.SetColumn(PowerStatusFrame, stacked ? 0 : 1);
        Grid.SetColumn(PcieStatusFrame, stacked ? 0 : 2);
        Grid.SetRow(PowerStatusFrame, stacked ? 1 : 0);
        Grid.SetRow(PcieStatusFrame, stacked ? 2 : 0);
    }

    private void BoostLimitsFlyout_Opening(object sender, object args)
    {
        if (sender is Flyout { Content: FrameworkElement content })
        {
            content.RequestedTheme = ActualTheme;
        }
    }

    private void RebuildMetrics(object? sender, NotifyCollectionChangedEventArgs? args)
    {
        MetricsHost.Children.Clear();
        var metrics = ShowSummary ? Model.DetailedMetrics : Model.Metrics;

        var valueMaxWidth = ShowSummary ? ColumnValueMaxWidth : 260;

        foreach (var metric in metrics.Concat(_additionalMetrics))
        {
            var isVramUsage = ReferenceEquals(metric, Model.Metrics[2]);
            MetricsHost.Children.Add(new MetricTile(metric, valueMaxWidth: isVramUsage ? VramValueMaxWidth : valueMaxWidth,
                usePanelBorder: true, singleLineValue: isVramUsage));
        }
    }

    private static void AddMetrics(StackPanel host, IEnumerable<MetricViewModel> metrics, double valueMaxWidth)
    {
        foreach (var metric in metrics)
        {
            host.Children.Add(new MetricTile(metric, valueMaxWidth: valueMaxWidth, usePanelBorder: true));
        }
    }
}
