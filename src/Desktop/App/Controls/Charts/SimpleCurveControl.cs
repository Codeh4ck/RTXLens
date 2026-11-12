using Avalonia;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace RTXLens.App.Controls.Charts;

public sealed record XyCurveEditingOptions(
    double XStep = 0,
    double YStep = 0,
    double MinimumXSpacing = 0,
    int MinimumPointCount = 2,
    int MaximumPointCount = int.MaxValue,
    bool CanMoveX = true,
    bool CanMoveY = true,
    bool CanInsertPoints = true,
    bool CanRemovePoints = true,
    bool ExtendEndpoints = false);

public sealed class SimpleCurveControl : XyPlot
{
    private const double HandleRadius = 6;
    private const double HitRadius = 12;

    public static readonly StyledProperty<IReadOnlyList<Point>> PointsProperty =
        AvaloniaProperty.Register<SimpleCurveControl, IReadOnlyList<Point>>(nameof(Points), [],
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<XyCurveEditingOptions> EditingOptionsProperty =
        AvaloniaProperty.Register<SimpleCurveControl, XyCurveEditingOptions>(nameof(EditingOptions), new XyCurveEditingOptions());

    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<SimpleCurveControl, bool>(nameof(IsReadOnly));

    private IPointer? _dragPointer;
    private Point? _pendingPoint;
    private Point _dragOrigin;
    private int _selectedIndex = -1;
    private int _hoveredIndex = -1;

    static SimpleCurveControl()
    {
        AffectsRender<SimpleCurveControl>(PointsProperty, EditingOptionsProperty, IsReadOnlyProperty);
    }

    /// <summary>Finite control points in strictly increasing X order. Replace the list to publish external edits.</summary>
    public IReadOnlyList<Point> Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public XyCurveEditingOptions EditingOptions
    {
        get => GetValue(EditingOptionsProperty);
        set => SetValue(EditingOptionsProperty, value);
    }

    public bool IsReadOnly
    {
        get => GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    public bool TryMovePoint(int index, Point position)
    {
        if (!TryConstrainMove(index, position, out var point) || point == Points[index])
        {
            return false;
        }

        var next = Points.ToArray();
        next[index] = point;
        SetCurrentValue(PointsProperty, next);
        return true;
    }

    public bool TryInsertPoint(Point position)
    {
        if (IsReadOnly || !EditingOptions.CanInsertPoints || Points.Count >= EditingOptions.MaximumPointCount || !IsFinite(position))
        {
            return false;
        }

        var x = Snap(position.X, BoundsFor(XAxis.Range), XAxis.Range, EditingOptions.XStep);
        var index = 0;
        while (index < Points.Count && Points[index].X < x)
        {
            index++;
        }

        if (!TryGetXLimits(index - 1, index, out var limits) || (x < limits.Minimum || x > limits.Maximum))
        {
            return false;
        }

        var y = Snap(position.Y, BoundsFor(YAxis.Range), YAxis.Range, EditingOptions.YStep);
        var next = new List<Point>(Points);
        next.Insert(index, new Point(x, y));
        SetCurrentValue(PointsProperty, [.. next]);
        SelectPoint(index);
        return true;
    }

    public bool TryRemovePoint(int index)
    {
        if (IsReadOnly || !EditingOptions.CanRemovePoints || index < 0 || index >= Points.Count
            || Points.Count <= EditingOptions.MinimumPointCount)
        {
            return false;
        }

        var next = new List<Point>(Points);
        next.RemoveAt(index);
        SetCurrentValue(PointsProperty, [.. next]);
        SelectPoint(Math.Min(index, next.Count - 1));
        return true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == PointsProperty || change.Property == EditingOptionsProperty || change.Property == IsReadOnlyProperty
            || change.Property == XAxisProperty || change.Property == YAxisProperty)
        {
            EndDrag();
            SetHoveredPoint(-1);
            SelectPoint(Math.Min(_selectedIndex, Points.Count - 1));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var position = e.GetCurrentPoint(this);
        var layout = GetPlotLayout();
        if (IsReadOnly || _dragPointer is not null || !position.Properties.IsLeftButtonPressed || !layout.IsDrawable)
        {
            return;
        }

        var pointIndex = HitTestPoint(layout, position.Position);
        if (pointIndex >= 0)
        {
            Focus();
            SelectPoint(pointIndex);
            _dragOrigin = position.Position;
            _dragPointer = e.Pointer;
            e.Pointer.Capture(this);
            e.Handled = true;
        }
        else if (layout.Plot.Contains(position.Position) && e.ClickCount == 2)
        {
            e.Handled = TryInsertPoint(layout.ToData(position.Position));
            if (e.Handled)
            {
                Focus();
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var position = e.GetPosition(this);
        if (e.Pointer == _dragPointer)
        {
            UpdateDrag(position);
            e.Handled = true;
        }
        else if (_dragPointer is null)
        {
            UpdateHover(position);
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHoveredPoint(-1);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (e.Pointer != _dragPointer)
        {
            return;
        }

        var position = e.GetPosition(this);
        UpdateDrag(position);

        var point = _pendingPoint;
        var index = _selectedIndex;
        EndDrag();

        if (point is { } value)
        {
            TryMovePoint(index, value);
        }

        UpdateHover(position);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (IsReadOnly || Points.Count == 0)
        {
            return;
        }

        if (e.Key == Key.Escape && _dragPointer is not null)
        {
            EndDrag();
            e.Handled = true;
            return;
        }

        if (_dragPointer is not null)
        {
            return;
        }

        if ((e.KeyModifiers & KeyModifiers.Control) != 0 && e.Key is Key.Left or Key.Right)
        {
            SelectPoint(Math.Clamp(_selectedIndex + (e.Key == Key.Left ? -1 : 1), 0, Points.Count - 1));
            e.Handled = true;
            return;
        }

        if (_selectedIndex < 0)
        {
            SelectPoint(0);
        }

        var point = Points[_selectedIndex];
        var xStep = StepOrDefault(EditingOptions.XStep, XAxis.Range);
        var yStep = StepOrDefault(EditingOptions.YStep, YAxis.Range);
        e.Handled = e.Key switch
        {
            Key.Left => TryMovePoint(_selectedIndex, point.WithX(point.X - xStep)),
            Key.Right => TryMovePoint(_selectedIndex, point.WithX(point.X + xStep)),
            Key.Up => TryMovePoint(_selectedIndex, point.WithY(point.Y + yStep)),
            Key.Down => TryMovePoint(_selectedIndex, point.WithY(point.Y - yStep)),
            Key.Delete => TryRemovePoint(_selectedIndex),
            _ => false
        };
    }

    private protected override void DrawPlot(DrawingContext context, XyPlotLayout layout)
    {
        if (Points.Count == 0)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            var first = ScreenPoint(layout, 0);
            var start = EditingOptions.ExtendEndpoints ? first.WithX(layout.Plot.Left) : first;

            path.BeginFigure(start, isFilled: false);
            if (start != first)
            {
                path.LineTo(first);
            }

            for (var index = 1; index < Points.Count; index++)
            {
                path.LineTo(ScreenPoint(layout, index));
            }

            if (EditingOptions.ExtendEndpoints)
            {
                path.LineTo(ScreenPoint(layout, Points.Count - 1).WithX(layout.Plot.Right));
            }

            path.EndFigure(isClosed: false);
        }

        context.DrawGeometry(null, new Pen(AccentBrush, 2), geometry);
    }

    private protected override void DrawOverlay(DrawingContext context, XyPlotLayout layout)
    {
        var normalPen = new Pen(AccentBrush, 1.5);
        var selectedPen = new Pen(AxisBrush, 2);

        for (var index = 0; index < Points.Count; index++)
        {
            var screen = ScreenPoint(layout, index);
            if (!layout.Plot.Contains(screen))
            {
                continue;
            }

            var selected = index == _selectedIndex;
            var radius = selected ? HandleRadius + 1 : HandleRadius;
            context.DrawEllipse(selected ? AccentBrush : PlotBackground, selected ? selectedPen : normalPen, screen, radius, radius);
        }

        var readoutPoint = _pendingPoint;
        if (readoutPoint is null && _hoveredIndex >= 0 && _hoveredIndex < Points.Count)
        {
            readoutPoint = Points[_hoveredIndex];
        }

        if (readoutPoint is { } point)
        {
            var anchor = layout.ToScreen(point);
            if (layout.Plot.Contains(anchor))
            {
                DrawReadout(context, anchor,
                    [CoordinateRow(YAxis, "Y", point.Y), CoordinateRow(XAxis, "X", point.X)]);
            }
        }
    }

    private ReadoutRow CoordinateRow(XyAxis axis, string fallbackLabel, double value)
    {
        var label = string.IsNullOrWhiteSpace(axis.Title) ? fallbackLabel : axis.Title;
        return new ReadoutRow(null, XyPlotLayout.Text(label, ReadoutForeground, size: 13),
            XyPlotLayout.Text(axis.FormatReadout(value), ReadoutForeground, size: 13, emphasized: true));
    }

    private void UpdateHover(Point position)
    {
        var layout = GetPlotLayout();
        SetHoveredPoint(layout.IsDrawable ? HitTestPoint(layout, position) : -1);
    }

    private void SetHoveredPoint(int index)
    {
        if (_hoveredIndex != index)
        {
            _hoveredIndex = index;
            InvalidateVisual();
        }
    }

    private void UpdateDrag(Point position)
    {
        if (_pendingPoint is null && DistanceSquared(position, _dragOrigin) < 4)
        {
            return;
        }

        var layout = GetPlotLayout();
        if (layout.IsDrawable && TryConstrainMove(_selectedIndex, layout.ToData(position), out var point) && point != _pendingPoint)
        {
            _pendingPoint = point;
            InvalidateVisual();
        }
    }

    private void EndDrag()
    {
        if (_dragPointer is not { } pointer)
        {
            return;
        }

        _dragPointer = null;
        _pendingPoint = null;
        if (pointer.Captured == this)
        {
            pointer.Capture(null);
        }

        InvalidateVisual();
    }

    private void SelectPoint(int index)
    {
        if (_selectedIndex != index)
        {
            _selectedIndex = index;
            InvalidateVisual();
        }
    }

    private Point ScreenPoint(XyPlotLayout layout, int index) =>
        layout.ToScreen(index == _selectedIndex && _pendingPoint is { } point ? point : Points[index]);

    private int HitTestPoint(XyPlotLayout layout, Point position)
    {
        var nearestIndex = -1;
        var nearestDistance = HitRadius * HitRadius;

        for (var index = 0; index < Points.Count; index++)
        {
            var point = ScreenPoint(layout, index);
            if (!layout.Plot.Contains(point))
            {
                continue;
            }

            var distance = DistanceSquared(point, position);
            if (distance <= nearestDistance)
            {
                nearestIndex = index;
                nearestDistance = distance;
            }
        }

        return nearestIndex;
    }

    private bool TryConstrainMove(int index, Point position, out Point point)
    {
        point = default;
        if (IsReadOnly || index < 0 || index >= Points.Count || !IsFinite(position)
            || !TryGetXLimits(index - 1, index + 1, out var limits))
        {
            return false;
        }

        var existing = Points[index];
        var x = EditingOptions.CanMoveX ? Snap(position.X, limits, XAxis.Range, EditingOptions.XStep) : existing.X;
        var y = EditingOptions.CanMoveY ? Snap(position.Y, BoundsFor(YAxis.Range), YAxis.Range, EditingOptions.YStep) : existing.Y;

        point = new Point(x, y);
        return true;
    }

    private bool TryGetXLimits(int previous, int next, out (double Minimum, double Maximum) limits)
    {
        var axis = XAxis.Range;
        var spacing = Math.Max(0, EditingOptions.MinimumXSpacing);
        var minimum = previous < 0 ? axis.Minimum : Math.Max(axis.Minimum, Math.Max(
            Points[previous].X + spacing, double.BitIncrement(Points[previous].X)));
        var maximum = next >= Points.Count ? axis.Maximum : Math.Min(axis.Maximum, Math.Min(
            Points[next].X - spacing, double.BitDecrement(Points[next].X)));

        // A point may have exactly one valid X coordinate between its neighbors.
        if (!(minimum <= maximum) || !double.IsFinite(minimum) || !double.IsFinite(maximum))
        {
            limits = default;
            return false;
        }

        limits = (minimum, maximum);
        return true;
    }

    private static double Snap(double value, (double Minimum, double Maximum) limits, XyRange axis, double step)
    {
        var clamped = Math.Clamp(value, limits.Minimum, limits.Maximum);
        if (step <= 0 || !double.IsFinite(step) || !double.IsFinite(axis.Span / step))
        {
            return clamped;
        }

        var minimumIndex = Math.Ceiling((limits.Minimum - axis.Minimum) / step);
        var maximumIndex = Math.Floor((limits.Maximum - axis.Minimum) / step);
        if (minimumIndex > maximumIndex)
        {
            return clamped;
        }

        var roundedIndex = Math.Round((clamped - axis.Minimum) / step, MidpointRounding.AwayFromZero);
        var index = Math.Clamp(roundedIndex, minimumIndex, maximumIndex);
        return Math.Clamp(axis.Minimum + index * step, limits.Minimum, limits.Maximum);
    }

    private static (double Minimum, double Maximum) BoundsFor(XyRange range) => (range.Minimum, range.Maximum);

    private static double StepOrDefault(double step, XyRange range) => step > 0 && double.IsFinite(step) ? step : range.Span / 100;

    private static double DistanceSquared(Point first, Point second)
    {
        var x = first.X - second.X;
        var y = first.Y - second.Y;
        return x * x + y * y;
    }

    private static bool IsFinite(Point point) => double.IsFinite(point.X) && double.IsFinite(point.Y);
}