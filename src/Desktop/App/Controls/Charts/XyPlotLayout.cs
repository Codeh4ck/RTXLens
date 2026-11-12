using Avalonia;
using Avalonia.Media;

namespace RTXLens.App.Controls.Charts;

internal sealed class XyPlotLayout
{
    private const double Padding = 10;
    private const double TickGap = 8;
    private const double XAxisTitleSpace = 19;
    private const double YAxisTitleTop = 4;
    private const double YAxisTitleGap = 17;

    private readonly AxisTick[] _verticalTicks;
    private readonly FormattedText? _horizontalTitle;
    private readonly FormattedText? _verticalTitle;

    public XyPlotLayout(Size size, XyAxis xAxis, XyAxis yAxis, XyRange visibleX, IBrush labelBrush)
    {
        X = visibleX;
        Y = yAxis.Range;

        _horizontalTitle = Title(xAxis.Title, labelBrush);
        _verticalTitle = Title(yAxis.Title, labelBrush);

        var top = _verticalTitle is null ? Padding : YAxisTitleTop + _verticalTitle.Height + YAxisTitleGap;
        var height = Math.Max(0, size.Height - top - 26 - (_horizontalTitle is null ? 0 : XAxisTitleSpace));
        _verticalTicks = CreateTicks(yAxis, Y, height, 28, labelBrush);

        var maxYLabel = _verticalTicks.Length == 0 ? 0 : _verticalTicks.Max(tick => tick.Text.Width);
        var firstXLabel = Text(xAxis.Format(X.Minimum), labelBrush).Width;
        var lastXLabel = Text(xAxis.Format(X.Maximum), labelBrush).Width;
        var left = Math.Max(Padding + maxYLabel + TickGap, firstXLabel / 2 + Padding);
        var right = Math.Max(Padding, lastXLabel / 2 + Padding);
        var width = Math.Max(0, size.Width - left - right);

        var preferredXLabels = Math.Max(firstXLabel, lastXLabel) + 20;
        HorizontalTicks = CreateTicks(xAxis, X, width, Math.Max(58, preferredXLabels), labelBrush);

        Plot = new Rect(left, top, width, height);
    }

    public XyRange X { get; }
    public XyRange Y { get; }
    public Rect Plot { get; }
    internal IReadOnlyList<AxisTick> HorizontalTicks { get; }

    public bool IsDrawable => Plot is { Width: >= 1, Height: >= 1 };

    public double ToScreenX(double x) => Plot.Left + (x - X.Minimum) / X.Span * Plot.Width;
    public double ToScreenY(double y) => Plot.Bottom - (y - Y.Minimum) / Y.Span * Plot.Height;

    public Point ToScreen(Point point) => new(ToScreenX(point.X), ToScreenY(point.Y));

    public Point ToData(Point position) => new(
        X.Minimum + Math.Clamp((position.X - Plot.Left) / Plot.Width, 0, 1) * X.Span,
        Y.Minimum + Math.Clamp((Plot.Bottom - position.Y) / Plot.Height, 0, 1) * Y.Span);

    public void DrawAxes(DrawingContext context, IBrush gridBrush)
    {
        var pen = new Pen(gridBrush);

        foreach (var tick in _verticalTicks)
        {
            var y = ToScreenY(tick.Value);
            context.DrawLine(pen, new Point(Plot.Left, y), new Point(Plot.Right, y));
            context.DrawText(tick.Text, new Point(Plot.Left - TickGap - tick.Text.Width, y - tick.Text.Height / 2));
        }

        foreach (var tick in HorizontalTicks)
        {
            var x = ToScreenX(tick.Value);
            context.DrawLine(pen, new Point(x, Plot.Top), new Point(x, Plot.Bottom));
            context.DrawText(tick.Text, new Point(x - tick.Text.Width / 2, Plot.Bottom + TickGap));
        }

        if (_verticalTitle is not null)
        {
            context.DrawText(_verticalTitle, new Point(Padding, YAxisTitleTop));
        }

        if (_horizontalTitle is not null)
        {
            var x = Plot.Center.X - _horizontalTitle.Width / 2;
            context.DrawText(_horizontalTitle, new Point(x, Plot.Bottom + 27));
        }
    }

    internal static FormattedText Text(string value, IBrush brush, double size = 12, bool emphasized = false) =>
        new(value, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI", weight: emphasized ? FontWeight.SemiBold : FontWeight.Normal), size, brush);

    private static FormattedText? Title(string title, IBrush brush) =>
        string.IsNullOrWhiteSpace(title) ? null : Text(title, brush, emphasized: true);

    private static AxisTick[] CreateTicks(XyAxis axis, XyRange range, double extent, double minimumSpacing, IBrush brush)
    {
        var maximumCount = (int)Math.Clamp(extent / minimumSpacing + 1, 2, 21);
        var intervals = Math.Clamp(axis.TickCount, 2, 21) - 1;
        if (axis.TickGranularity is double granularity and > 0 and < double.PositiveInfinity)
        {
            return CreateAlignedTicks(axis, range, granularity, Math.Min(intervals + 1, maximumCount), brush);
        }

        var stride = (int)Math.Ceiling((double)intervals / (maximumCount - 1));
        var ticks = new List<AxisTick>(maximumCount);

        for (var index = 0; index < intervals; index += stride)
        {
            if (index != 0 && intervals - index < stride * 0.75)
            {
                break;
            }

            var value = range.Minimum + range.Span * ((double)index / intervals);
            ticks.Add(new AxisTick(value, Text(axis.Format(value), brush)));
        }

        ticks.Add(new AxisTick(range.Maximum, Text(axis.Format(range.Maximum), brush)));
        return [.. ticks];
    }

    private static AxisTick[] CreateAlignedTicks(XyAxis axis, XyRange range, double granularity, int count, IBrush brush)
    {
        var step = Math.Max(1, Math.Ceiling(range.Span / (count - 1) / granularity)) * granularity;
        var firstIndex = Math.Ceiling(range.Minimum / step);
        var ticks = new List<AxisTick>(count);

        for (var offset = 0; offset < count; offset++)
        {
            var value = (firstIndex + offset) * step;
            if (!range.Contains(value))
            {
                break;
            }

            ticks.Add(new AxisTick(value, Text(axis.Format(value), brush)));
        }

        return [.. ticks];
    }

    internal readonly record struct AxisTick(double Value, FormattedText Text);
}