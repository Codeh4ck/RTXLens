using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Media;

namespace RTXLens.App.Controls.Charts;

public abstract class XyPlot : Control
{
    public static readonly StyledProperty<XyAxis> XAxisProperty = AvaloniaProperty.Register<XyPlot, XyAxis>(nameof(XAxis), XyAxis.Default);
    public static readonly StyledProperty<XyAxis> YAxisProperty = AvaloniaProperty.Register<XyPlot, XyAxis>(nameof(YAxis), XyAxis.Default);

    public static readonly StyledProperty<IReadOnlyList<XyGuide>> GuidesProperty =
        AvaloniaProperty.Register<XyPlot, IReadOnlyList<XyGuide>>(nameof(Guides), []);

    public static readonly StyledProperty<IReadOnlyList<XyMarker>> MarkersProperty =
        AvaloniaProperty.Register<XyPlot, IReadOnlyList<XyMarker>>(nameof(Markers), []);

    public static readonly StyledProperty<IBrush> AxisBrushProperty =
        AvaloniaProperty.Register<XyPlot, IBrush>(nameof(AxisBrush), Brushes.Gray);

    public static readonly StyledProperty<IBrush> GridBrushProperty =
        AvaloniaProperty.Register<XyPlot, IBrush>(nameof(GridBrush), Brushes.DarkSlateGray);

    public static readonly StyledProperty<IBrush> AccentBrushProperty =
        AvaloniaProperty.Register<XyPlot, IBrush>(nameof(AccentBrush), Brushes.Turquoise);

    public static readonly StyledProperty<IBrush> PlotBackgroundProperty =
        AvaloniaProperty.Register<XyPlot, IBrush>(nameof(PlotBackground), Brushes.Transparent);

    public static readonly StyledProperty<IBrush> ReadoutBackgroundProperty =
        AvaloniaProperty.Register<XyPlot, IBrush>(nameof(ReadoutBackground), Brushes.Black);

    public static readonly StyledProperty<IBrush> ReadoutForegroundProperty =
        AvaloniaProperty.Register<XyPlot, IBrush>(nameof(ReadoutForeground), Brushes.White);

    private (Size Size, XyAxis X, XyAxis Y, XyRange View, IBrush Brush) _layoutKey;
    private XyPlotLayout? _layout;

    static XyPlot()
    {
        AffectsRender<XyPlot>(XAxisProperty, YAxisProperty, GuidesProperty, MarkersProperty, AxisBrushProperty,
            GridBrushProperty, AccentBrushProperty, PlotBackgroundProperty, ReadoutBackgroundProperty, ReadoutForegroundProperty);
        FocusableProperty.OverrideDefaultValue<XyPlot>(true);
    }

    public XyAxis XAxis
    {
        get => GetValue(XAxisProperty);
        set => SetValue(XAxisProperty, value);
    }

    public XyAxis YAxis
    {
        get => GetValue(YAxisProperty);
        set => SetValue(YAxisProperty, value);
    }

    public IReadOnlyList<XyGuide> Guides
    {
        get => GetValue(GuidesProperty);
        set => SetValue(GuidesProperty, value);
    }

    public IReadOnlyList<XyMarker> Markers
    {
        get => GetValue(MarkersProperty);
        set => SetValue(MarkersProperty, value);
    }

    public IBrush AxisBrush
    {
        get => GetValue(AxisBrushProperty);
        set => SetValue(AxisBrushProperty, value);
    }

    public IBrush GridBrush
    {
        get => GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public IBrush AccentBrush
    {
        get => GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public IBrush PlotBackground
    {
        get => GetValue(PlotBackgroundProperty);
        set => SetValue(PlotBackgroundProperty, value);
    }

    public IBrush ReadoutBackground
    {
        get => GetValue(ReadoutBackgroundProperty);
        set => SetValue(ReadoutBackgroundProperty, value);
    }

    public IBrush ReadoutForeground
    {
        get => GetValue(ReadoutForegroundProperty);
        set => SetValue(ReadoutForegroundProperty, value);
    }

    private protected virtual XyRange DisplayedX => XAxis.Range;

    private protected virtual double FooterHeight(double availableWidth) => 0;

    internal XyPlotLayout GetPlotLayout()
    {
        var footer = FooterHeight(Bounds.Width);
        var size = new Size(Bounds.Width, Math.Max(0, Bounds.Height - footer));
        var key = (size, XAxis, YAxis, DisplayedX, AxisBrush);

        if (_layout is null || key != _layoutKey)
        {
            _layout = new XyPlotLayout(size, key.XAxis, key.YAxis, key.DisplayedX, key.AxisBrush);
            _layoutKey = key;
        }

        return _layout;
    }

    public sealed override void Render(DrawingContext context)
    {
        base.Render(context);

        var layout = GetPlotLayout();
        if (!layout.IsDrawable)
        {
            return;
        }

        context.FillRectangle(PlotBackground, layout.Plot);
        layout.DrawAxes(context, GridBrush);

        using (context.PushClip(layout.Plot))
        {
            DrawGuides(context, layout);
            DrawPlot(context, layout);
        }

        DrawMarkers(context, layout);
        DrawOverlay(context, layout);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _layout = null;
        _layoutKey = default;
        base.OnDetachedFromVisualTree(e);
    }

    private protected abstract void DrawPlot(DrawingContext context, XyPlotLayout layout);

    private protected virtual void DrawOverlay(DrawingContext context, XyPlotLayout layout)
    {
    }

    private protected void DrawReadout(
        DrawingContext context, Point anchor, IReadOnlyList<ReadoutRow> rows, FormattedText? title = null)
    {
        const double padding = 12;
        const double swatchWidth = 10;
        const double labelOffset = swatchWidth + 8;
        const double valueGap = 20;
        const double minimumValueWidth = 60;
        const double columnGap = 24;
        const double headingGap = 7;

        var count = rows.Count;
        if (count == 0)
        {
            return;
        }

        var contentWidth = Math.Max(1, Bounds.Width - 2 * padding);
        if (title is not null)
        {
            FitText(title, contentWidth);
        }

        var headingHeight = title is null ? 0 : title.Height + headingGap;
        var titleWidth = title?.Width ?? 0;
        var rowHeight = rows.Max(row => Math.Max(row.Label.Height, row.Value.Height)) + 5;
        var availableHeight = Bounds.Height - 2 * padding - headingHeight;
        var maximumRows = Math.Max(1, (int)(availableHeight / rowHeight));
        var columns = (count + maximumRows - 1) / maximumRows;
        var rowsPerColumn = (count + columns - 1) / columns;
        var maximumColumnWidth = Math.Max(1, (contentWidth - (columns - 1) * columnGap) / columns);
        var valueWidth = Math.Max(minimumValueWidth, rows.Max(row => row.Value.Width));
        var labelWidth = rows.Max(row => row.Label.Width);

        var preferredColumnWidth = Math.Max(titleWidth / columns, labelOffset + labelWidth + valueGap + valueWidth);
        var columnWidth = Math.Min(maximumColumnWidth, preferredColumnWidth);
        var visibleValueWidth = Math.Min(valueWidth, Math.Max(1, columnWidth - labelOffset - valueGap));
        var visibleLabelWidth = Math.Max(1, columnWidth - labelOffset - valueGap - visibleValueWidth);

        var width = Math.Max(titleWidth, columns * columnWidth + (columns - 1) * columnGap) + 2 * padding;
        var height = 2 * padding + headingHeight + rowsPerColumn * rowHeight - 5;
        var bounds = PlaceReadout(anchor, new Size(width, height));

        using var viewportClip = context.PushClip(new Rect(Bounds.Size));
        context.DrawRectangle(ReadoutBackground, new Pen(AccentBrush), bounds, 7, 7);
        if (title is not null)
        {
            context.DrawText(title, bounds.TopLeft + new Vector(padding, padding));
        }

        for (var index = 0; index < count; index++)
        {
            var row = rows[index];
            var x = bounds.X + padding + index / rowsPerColumn * (columnWidth + columnGap);
            var y = bounds.Y + padding + headingHeight + index % rowsPerColumn * rowHeight;
            var centerY = y + (rowHeight - 5) / 2;

            FitText(row.Label, visibleLabelWidth);
            FitText(row.Value, visibleValueWidth);
            if (row.Swatch is { } swatch)
            {
                context.DrawRectangle(swatch, null, new Rect(x, centerY - 1.5, swatchWidth, 3), 1, 1);
            }
            else
            {
                context.DrawEllipse(ReadoutForeground, null, new Point(x + swatchWidth / 2, centerY), 2, 2);
            }


            context.DrawText(row.Label, new Point(x + labelOffset, centerY - row.Label.Height / 2));
            context.DrawText(row.Value, new Point(x + columnWidth - row.Value.Width, centerY - row.Value.Height / 2));
        }
    }

    private Rect PlaceReadout(Point anchor, Size size)
    {
        const double offset = 14;
        var left = anchor.X + offset + size.Width <= Bounds.Width ? anchor.X + offset : anchor.X - size.Width - offset;
        var top = anchor.Y + offset + size.Height <= Bounds.Height ? anchor.Y + offset : anchor.Y - size.Height - offset;
        var x = Math.Clamp(left, 0, Math.Max(0, Bounds.Width - size.Width));
        var y = Math.Clamp(top, 0, Math.Max(0, Bounds.Height - size.Height));

        return new Rect(new Point(x, y), size);
    }

    private static void FitText(FormattedText text, double width)
    {
        if (text.Width > width)
        {
            text.MaxTextWidth = width;
            text.MaxLineCount = 1;
            text.Trimming = TextTrimming.CharacterEllipsis;
        }
    }

    private protected readonly record struct ReadoutRow(IBrush? Swatch, FormattedText Label, FormattedText Value);

    private void DrawGuides(DrawingContext context, XyPlotLayout layout)
    {
        foreach (var guide in Guides)
        {
            var pen = new Pen(guide.Brush ?? AccentBrush, 1, DashStyle.Dash);
            if (guide.Axis == XyAxisDirection.X && layout.X.Contains(guide.Value))
            {
                var x = layout.ToScreenX(guide.Value);
                context.DrawLine(pen, new Point(x, layout.Plot.Top), new Point(x, layout.Plot.Bottom));
            }
            else if (guide.Axis == XyAxisDirection.Y && layout.Y.Contains(guide.Value))
            {
                var y = layout.ToScreenY(guide.Value);
                context.DrawLine(pen, new Point(layout.Plot.Left, y), new Point(layout.Plot.Right, y));
            }
        }
    }

    private void DrawMarkers(DrawingContext context, XyPlotLayout layout)
    {
        foreach (var marker in Markers)
        {
            if (!layout.X.Contains(marker.Position.X) || !layout.Y.Contains(marker.Position.Y)
                || !double.IsFinite(marker.Radius) || marker.Radius <= 0)
            {
                continue;
            }

            var brush = marker.Brush ?? AccentBrush;
            context.DrawEllipse(marker.Filled ? brush : PlotBackground, new Pen(brush, 2), layout.ToScreen(marker.Position),
                marker.Radius, marker.Radius);
        }
    }
}