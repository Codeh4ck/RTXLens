using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RTXLens.Presentation.Telemetry;

namespace RTXLens.App.Views;

public sealed partial class GpuStatusView : UserControl
{
    private const double MinimumThreeColumnWidth = 1240;
    private const double PanelSpacing = 12;

    public GpuStatusViewModel Model { get; }
    public bool ShowSummary { get; }
    public IReadOnlyList<MetricViewModel> AdditionalMetrics { get; }
    public bool HasAdditionalMetrics => AdditionalMetrics.Count > 0;

    private bool _stacked;

    internal GpuStatusView(GpuStatusViewModel model, bool showSummary, IReadOnlyList<MetricViewModel> additionalMetrics)
    {
        Model = model;
        ShowSummary = showSummary;
        AdditionalMetrics = additionalMetrics;
        InitializeComponent();

        if (showSummary)
        {
            InitializeStandaloneLayout();
        }
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

        StatusColumns.SizeChanged += OnStatusColumnsSizeChanged;
        Loaded += (_, _) =>
        {
            Model.Diagnostics.PropertyChanged += OnDiagnosticsChanged;
            UpdatePowerMarkers();
        };
        Unloaded += (_, _) => Model.Diagnostics.PropertyChanged -= OnDiagnosticsChanged;
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

    private void PowerScale_SizeChanged(object sender, SizeChangedEventArgs args) => UpdatePowerMarkers();

    private void OnDiagnosticsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(GpuDiagnosticsViewModel.PowerLimitPositions))
        {
            UpdatePowerMarkers();
        }
    }

    private void UpdatePowerMarkers()
    {
        const double trackInset = 5;

        var (enforced, defaultLimit) = Model.Diagnostics.PowerLimitPositions;
        var trackWidth = Math.Max(0, PowerScale.ActualWidth - trackInset * 2);

        Position(EnforcedPowerMarker, enforced);
        Position(DefaultPowerMarker, defaultLimit);

        void Position(FrameworkElement marker, double? fraction)
        {
            if (fraction is { } value && trackWidth > 0)
            {
                Canvas.SetLeft(marker, trackInset + trackWidth * value - marker.Width / 2);
                marker.Visibility = Visibility.Visible;
            }
            else
            {
                marker.Visibility = Visibility.Collapsed;
            }
        }
    }
}
