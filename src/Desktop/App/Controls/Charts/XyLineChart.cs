using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace RTXLens.App.Controls.Charts;

internal delegate void XyHoverLookup(double x, Span<XySample?> samples);

public sealed class XyLineChart : XyPlot
{
    public static readonly StyledProperty<IReadOnlyList<XySeries>> SeriesProperty =
        AvaloniaProperty.Register<XyLineChart, IReadOnlyList<XySeries>>(nameof(Series), []);

    public static readonly StyledProperty<XyRange?> VisibleXRangeProperty =
        AvaloniaProperty.Register<XyLineChart, XyRange?>(nameof(VisibleXRange), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> FollowLatestProperty =
        AvaloniaProperty.Register<XyLineChart, bool>(nameof(FollowLatest), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> ShowLegendProperty =
        AvaloniaProperty.Register<XyLineChart, bool>(nameof(ShowLegend), true);

    public static readonly StyledProperty<bool> ShowLegendValuesProperty =
        AvaloniaProperty.Register<XyLineChart, bool>(nameof(ShowLegendValues));

    public static readonly StyledProperty<bool> ShowHoverReadoutProperty =
        AvaloniaProperty.Register<XyLineChart, bool>(nameof(ShowHoverReadout), true);

    private (XyPlotLayout Layout, IReadOnlyList<XySeries> Series, double Scale) _geometryKey;
    private (double Width, IReadOnlyList<XySeries> Series, XyAxis YAxis, IBrush Brush, bool Values) _legendKey;
    private StreamGeometry[] _geometries = [];
    private LegendItem[] _legendItems = [];
    private XySample?[] _hoverSamples = [];
    private double _legendHeight;
    private Point? _hover;
    private IPointer? _panPointer;
    private Point _panOrigin;
    private XyRange? _panRange;
    private double _panWidth;

    static XyLineChart()
    {
        AffectsRender<XyLineChart>(SeriesProperty, VisibleXRangeProperty, ShowLegendProperty, ShowLegendValuesProperty,
            ShowHoverReadoutProperty);
    }

    public IReadOnlyList<XySeries> Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    /// <summary>The horizontal viewport. A null value displays the complete X axis.</summary>
    public XyRange? VisibleXRange
    {
        get => GetValue(VisibleXRangeProperty);
        set => SetValue(VisibleXRangeProperty, value);
    }

    public bool FollowLatest
    {
        get => GetValue(FollowLatestProperty);
        set => SetValue(FollowLatestProperty, value);
    }

    public bool ShowLegend
    {
        get => GetValue(ShowLegendProperty);
        set => SetValue(ShowLegendProperty, value);
    }

    public bool ShowLegendValues
    {
        get => GetValue(ShowLegendValuesProperty);
        set => SetValue(ShowLegendValuesProperty, value);
    }

    public bool ShowHoverReadout
    {
        get => GetValue(ShowHoverReadoutProperty);
        set => SetValue(ShowHoverReadoutProperty, value);
    }

    internal XyHoverLookup? HoverLookup { get; set; }

    private protected override XyRange DisplayedX =>
        VisibleXRange is { } range ? ClampViewport(range.Minimum, range.Span, XAxis.Range) : XAxis.Range;

    public void Fit()
    {
        SetCurrentValue(FollowLatestProperty, false);
        SetCurrentValue(VisibleXRangeProperty, null);
    }

    public void Zoom(double factor, double? anchor = null)
    {
        if (!double.IsFinite(factor) || factor <= 0 || factor == 1 || (anchor.HasValue && !double.IsFinite(anchor.Value)))
        {
            return;
        }

        var view = DisplayedX;
        var range = XAxis.Range;
        var center = view.Clamp(anchor ?? (view.Minimum + view.Span / 2));
        var relative = (center - view.Minimum) / view.Span;
        var span = Math.Clamp(view.Span / factor, Math.Max(double.Epsilon, range.Span / 1_000_000), range.Span);

        Navigate(center - span * relative, span);
    }

    /// <summary>Moves the viewport by a fraction of its current width.</summary>
    public void Pan(double fraction)
    {
        if (double.IsFinite(fraction) && fraction != 0)
        {
            var view = DisplayedX;
            Navigate(view.Minimum + view.Span * fraction, view.Span);
        }
    }

    public void GoToStart() => Navigate(XAxis.Range.Minimum, DisplayedX.Span);

    public void GoToEnd() => Navigate(XAxis.Range.Maximum - DisplayedX.Span, DisplayedX.Span);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == XAxisProperty || change.Property == FollowLatestProperty)
        {
            if (FollowLatest && VisibleXRange is { } view)
            {
                SetViewport(XAxis.Range.Maximum - view.Span, view.Span);
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var position = e.GetCurrentPoint(this);
        var layout = GetPlotLayout();
        if (_panPointer is not null || !position.Properties.IsLeftButtonPressed || !layout.Plot.Contains(position.Position))
        {
            return;
        }

        Focus();
        _panOrigin = position.Position;
        _panWidth = layout.Plot.Width;
        _panRange = layout.X;
        _panPointer = e.Pointer;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var position = e.GetPosition(this);
        if (e.Pointer == _panPointer && _panRange is { } initial && _panWidth > 0)
        {
            var distance = _panOrigin.X - position.X;
            if (Math.Abs(distance) >= 3 || DisplayedX != initial)
            {
                Navigate(initial.Minimum + distance / _panWidth * initial.Span, initial.Span);
                SetHover(null);
                e.Handled = true;
                return;
            }
        }

        SetHover(GetPlotLayout().Plot.Contains(position) ? position : null);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (e.Pointer == _panPointer)
        {
            _panPointer = null;
            _panRange = null;
            e.Pointer.Capture(null);
            var position = e.GetPosition(this);
            SetHover(GetPlotLayout().Plot.Contains(position) ? position : null);
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _panPointer = null;
        _panRange = null;
        SetHover(null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHover(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        var layout = GetPlotLayout();
        var position = e.GetPosition(this);
        if (!layout.IsDrawable || !layout.Plot.Contains(position))
        {
            return;
        }

        if ((e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            Zoom(Math.Pow(1.3, e.Delta.Y), layout.ToData(position).X);
        }
        else if (e.Delta.X != 0 || (e.KeyModifiers & KeyModifiers.Shift) != 0)
        {
            Pan(-(e.Delta.X != 0 ? e.Delta.X : e.Delta.Y) / 6);
        }
        else
        {
            return;
        }

        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        switch (e.Key)
        {
            case Key.Left:
                Pan(-0.2);
                break;
            case Key.Right:
                Pan(0.2);
                break;
            case Key.Home:
                GoToStart();
                break;
            case Key.End:
                GoToEnd();
                break;
            case Key.Add:
            case Key.OemPlus:
                Zoom(1.5);
                break;
            case Key.Subtract:
            case Key.OemMinus:
                Zoom(1 / 1.5);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _geometries = [];
        _geometryKey = default;
        _legendItems = [];
        _hoverSamples = [];
        _legendKey = default;
        _legendHeight = 0;
        _hover = null;
        base.OnDetachedFromVisualTree(e);
    }

    private protected override double FooterHeight(double availableWidth)
    {
        if (!ShowLegend || Series.Count == 0)
        {
            return 0;
        }

        var key = (availableWidth, Series, YAxis, AxisBrush, ShowLegendValues);
        if (_legendKey == key)
        {
            return _legendHeight;
        }

        const double margin = 10;
        const double rowHeight = 22;
        var x = margin;
        var y = 4.0;
        var items = new LegendItem[Series.Count];

        for (var index = 0; index < Series.Count; index++)
        {
            var series = Series[index];
            var label = XyPlotLayout.Text(series.Label, AxisBrush);
            var valueWidth = ShowLegendValues ? Math.Max(ValueLabel(series, YAxis.Range.Minimum).Width,
                ValueLabel(series, YAxis.Range.Maximum).Width) + 13 : 0;
            label.MaxTextWidth = Math.Max(1, availableWidth - 2 * margin - valueWidth - 30);
            label.Trimming = TextTrimming.CharacterEllipsis;
            var width = label.Width + valueWidth + 30;

            if (x > margin && x + width > availableWidth - margin)
            {
                x = margin;
                y += rowHeight;
            }

            items[index] = new LegendItem(new Point(x, y), label);
            x += width;
        }

        _legendItems = items;
        _legendKey = key;
        _legendHeight = y + rowHeight;
        return _legendHeight;
    }

    private protected override void DrawPlot(DrawingContext context, XyPlotLayout layout)
    {
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var key = (layout, Series, scale);
        if (_geometryKey != key)
        {
            var paths = new StreamGeometry[Series.Count];
            for (var index = 0; index < Series.Count; index++)
            {
                paths[index] = XySeriesPath.Build(Series[index].Samples.Span, layout, scale);
            }

            _geometries = paths;
            _geometryKey = key;
        }

        for (var index = 0; index < _geometries.Length; index++)
        {
            context.DrawGeometry(null, new Pen(Series[index].Brush, 1.6, lineCap: PenLineCap.Round), _geometries[index]);
        }
    }

    private protected override void DrawOverlay(DrawingContext context, XyPlotLayout layout)
    {
        var hover = ShowHoverReadout && _hover is { } point && layout.Plot.Contains(point) ? point : (Point?)null;
        var readoutX = layout.X.Maximum;
        ReadOnlySpan<XySample?> samples = [];

        if (hover is { } position)
        {
            var cursorX = layout.ToData(position).X;
            readoutX = HoverLookup is null ? FindReadoutX(layout, cursorX) : cursorX;
            samples = ReadHoverSamples(readoutX, layout.X);
        }

        if (ShowLegend && Series.Count > 0)
        {
            DrawLegend(context, layout, samples);
        }

        if (hover is { } anchor)
        {
            DrawHover(context, layout, anchor, readoutX, samples);
        }
    }

    internal ReadOnlySpan<XySample?> ReadHoverSamples(double x, XyRange visible)
    {
        if (_hoverSamples.Length != Series.Count)
        {
            _hoverSamples = new XySample?[Series.Count];
        }

        if (HoverLookup is { } lookup)
        {
            lookup(x, _hoverSamples);
        }
        else
        {
            for (var index = 0; index < Series.Count; index++)
            {
                _hoverSamples[index] = XySeriesPath.FindSample(Series[index].Samples.Span, x, visible, atOrBefore: false);
            }
        }

        return _hoverSamples;
    }

    private void DrawLegend(DrawingContext context, XyPlotLayout layout, ReadOnlySpan<XySample?> samples)
    {
        var top = Bounds.Height - _legendHeight;

        for (var index = 0; index < _legendItems.Length; index++)
        {
            var item = _legendItems[index];
            var series = Series[index];
            var y = top + item.Origin.Y;
            var left = item.Origin.X;
            context.DrawLine(new Pen(series.Brush, 2), new Point(left, y + 8), new Point(left + 13, y + 8));
            context.DrawText(item.Label, new Point(left + 19, y));

            if (ShowLegendValues)
            {
                var sample = samples.IsEmpty
                    ? XySeriesPath.FindSample(series.Samples.Span, layout.X.Maximum, layout.X, atOrBefore: true)
                    : samples[index];
                context.DrawText(ValueLabel(series, sample?.Y), new Point(left + 26 + item.Label.Width, y));
            }
        }
    }

    private void DrawHover(DrawingContext context, XyPlotLayout layout, Point anchor, double x, ReadOnlySpan<XySample?> samples)
    {
        var screenX = layout.ToScreenX(x);
        context.DrawLine(new Pen(AxisBrush, 1, DashStyle.Dash), new Point(screenX, layout.Plot.Top),
            new Point(screenX, layout.Plot.Bottom));

        if (Series.Count == 0)
        {
            return;
        }

        var title = XyPlotLayout.Text(XAxis.FormatReadout(x), ReadoutForeground, size: 13, emphasized: true);
        var rows = new ReadoutRow[Series.Count];

        for (var index = 0; index < Series.Count; index++)
        {
            var series = Series[index];
            var sample = samples[index];
            if (sample is { Y: { } value } point && layout.X.Contains(point.X) && layout.Y.Contains(value))
            {
                context.DrawEllipse(series.Brush, new Pen(PlotBackground, 1.5), layout.ToScreen(new Point(point.X, value)), 3, 3);
            }

            var label = XyPlotLayout.Text(series.Label, ReadoutForeground, size: 13);
            var reading = XyPlotLayout.Text(FormatValue(series, sample?.Y), ReadoutForeground, size: 13, emphasized: true);
            rows[index] = new ReadoutRow(series.Brush, label, reading);
        }

        DrawReadout(context, anchor.WithX(screenX), rows, title);
    }

    private double FindReadoutX(XyPlotLayout layout, double x)
    {
        var nearest = x;
        var distance = double.PositiveInfinity;

        foreach (var series in Series)
        {
            var sample = XySeriesPath.FindSample(series.Samples.Span, x, layout.X, atOrBefore: false);
            if (sample is { } value && Math.Abs(value.X - x) < distance)
            {
                nearest = value.X;
                distance = Math.Abs(value.X - x);
            }
        }

        return nearest;
    }

    private FormattedText ValueLabel(XySeries series, double? value) =>
        XyPlotLayout.Text(FormatValue(series, value), AxisBrush, emphasized: true);

    private string FormatValue(XySeries series, double? value) => value is { } reading && double.IsFinite(reading)
        ? series.FormatValue?.Invoke(reading) ?? YAxis.FormatReadout(reading)
        : "-";

    private void SetHover(Point? point)
    {
        if (_hover != point)
        {
            _hover = point;
            InvalidateVisual();
        }
    }

    private void Navigate(double minimum, double span)
    {
        SetCurrentValue(FollowLatestProperty, false);
        SetViewport(minimum, span);
    }

    private void SetViewport(double minimum, double span)
    {
        var range = XAxis.Range;
        var next = ClampViewport(minimum, span, range);
        SetCurrentValue(VisibleXRangeProperty, next == range ? null : next);
    }

    private static XyRange ClampViewport(double minimum, double span, XyRange range)
    {
        if (span >= range.Span)
        {
            return range;
        }

        var left = Math.Clamp(minimum, range.Minimum, Math.Max(range.Minimum, range.Maximum - span));
        var right = Math.Min(range.Maximum, left + span);
        return right > left ? new XyRange(left, right) : range;
    }

    private readonly record struct LegendItem(Point Origin, FormattedText Label);
}