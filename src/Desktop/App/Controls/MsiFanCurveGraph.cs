using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using RTXLens.Plugins.Msi.Contracts.Telemetry;
using RTXLens.Presentation.Configuration;
using Windows.Foundation;

namespace RTXLens.App.Controls;

internal sealed partial class MsiFanCurveGraph : Grid, IDisposable
{
    private readonly Canvas _plot = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private readonly Canvas _live = new() { IsHitTestVisible = false };
    private readonly Line _temperature = new() { StrokeDashArray = [4, 4], StrokeThickness = 1 };
    private readonly Ellipse _readback = new() { Width = 14, Height = 14, StrokeThickness = 2 };
    private readonly Ellipse _target = new() { Width = 10, Height = 10, StrokeThickness = 2 };
    private readonly SolidColorBrush _accent = new(Colors.Turquoise);
    private readonly SolidColorBrush _text = new(Colors.LightGray);
    private readonly SolidColorBrush _grid = new(Colors.SlateGray);
    private readonly Canvas _dragOverlay = new() { IsHitTestVisible = false };
    private readonly SolidColorBrush _readoutBackground = new(Colors.Black);
    private readonly TextBlock _dragText = new()
    {
        Name = "DragPointReadoutText",
        FontSize = 14,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
    };
    private readonly Border _dragReadout = new()
    {
        Name = "DragPointReadout",
        Padding = new Thickness(10, 6, 10, 6),
        CornerRadius = new CornerRadius(5),
        BorderThickness = new Thickness(1),
        Visibility = Visibility.Collapsed
    };
    private MsiSettingsEditorViewModel? _model;
    private MsiFanCurvePointViewModel? _dragged;
    private MsiSnapshot? _snapshot;

    private double PlotWidth => Math.Max(1, ActualWidth - 80);
    private double PlotHeight => Math.Max(1, ActualHeight - 85);
    private double X(double value) => 55 + Math.Clamp(value, 0, 100) / 100 * PlotWidth;
    private double Y(double value) => 20 + (100 - Math.Clamp(value, 0, 100)) / 100 * PlotHeight;

    public MsiFanCurveGraph()
    {
        Height = 380;
        Children.Add(_plot);
        Children.Add(_live);
        Children.Add(_dragOverlay);
        _dragOverlay.Children.Add(_dragReadout);
        _dragReadout.Child = _dragText;
        _dragReadout.Background = _readoutBackground;
        _dragReadout.BorderBrush = _accent;
        _dragText.Foreground = _text;
        _live.Children.Add(_temperature);
        _live.Children.Add(_readback);
        _live.Children.Add(_target);

        _temperature.Stroke = _accent;
        _readback.Stroke = _accent;
        _readback.Fill = _accent;
        _target.Stroke = new SolidColorBrush(Colors.Orchid);
        _target.Fill = new SolidColorBrush(Colors.Transparent);

        SizeChanged += (_, _) => Draw();
        ActualThemeChanged += (_, _) => Draw();
        _plot.PointerMoved += OnPointerMoved;
        _plot.PointerReleased += OnPointerReleased;
        _plot.PointerCanceled += OnPointerReleased;
        _plot.PointerCaptureLost += (_, _) => HideDragReadout();
        Unloaded += (_, _) => EndDrag();
        _plot.DoubleTapped += OnDoubleTapped;
    }

    public void SetModel(MsiSettingsEditorViewModel model)
    {
        EndDrag();

        if (_model is not null)
        {
            _model.Edited -= Draw;
        }

        _model = model;
        _model.Edited += Draw;
        Draw();
    }

    public void UpdateLive(MsiSnapshot? snapshot)
    {
        _snapshot = snapshot;
        PositionLive();
    }

    private void Draw()
    {
        _text.Color = ActualTheme == ElementTheme.Light ? Colors.DarkSlateGray : Colors.LightGray;
        _grid.Color = ActualTheme == ElementTheme.Light ? Colors.LightSlateGray : Colors.DarkSlateGray;
        _readoutBackground.Color = ActualTheme == ElementTheme.Light ? Colors.White : Colors.Black;
        _plot.Children.Clear();

        for (var tick = 0; tick <= 100; tick += 10)
        {
            var y = Y(tick);
            _plot.Children.Add(new Line { X1 = X(0), X2 = X(100), Y1 = y, Y2 = y, Stroke = _grid, StrokeThickness = 1 });
            Label(tick + "%", 5, y - 9);
            Label(tick + " °C", X(tick), Y(0) + 10, centered: true);
        }

        Label("PSU temperature (°C)", (X(0) + X(100)) / 2, ActualHeight - 22, centered: true);

        if (_model is not null)
        {
            var line = new Polyline { Stroke = _accent, StrokeThickness = 2, IsHitTestVisible = false };

            var points = _model.CurvePoints.Where(point => double.IsFinite(point.TemperatureC) && double.IsFinite(point.DutyPercent)).ToArray();

            if (points.Length > 0)
            {
                line.Points.Add(new Point(X(0), Y(points[0].DutyPercent)));

                foreach (var point in points)
                {
                    line.Points.Add(new Point(X(point.TemperatureC), Y(point.DutyPercent)));
                }

                line.Points.Add(new Point(X(100), Y(points[^1].DutyPercent)));
            }

            _plot.Children.Add(line);

            foreach (var point in _model.CurvePoints.Where(point => double.IsFinite(point.TemperatureC) && double.IsFinite(point.DutyPercent)))
            {
                var handle = new Ellipse { Width = 18, Height = 18, Fill = _accent, Stroke = _text, StrokeThickness = 2 };
                Canvas.SetLeft(handle, X(point.TemperatureC) - 9);
                Canvas.SetTop(handle, Y(point.DutyPercent) - 9);
                ToolTipService.SetToolTip(handle, $"{point.TemperatureC:0}°C / {point.DutyPercent:0}%");

                handle.PointerPressed += (_, args) =>
                {
                    BeginDrag(point);

                    if (!_plot.CapturePointer(args.Pointer))
                    {
                        EndDrag();
                    }
                    args.Handled = true;
                };

                _plot.Children.Add(handle);
            }
        }

        PositionLive();
        PositionDragReadout();
    }

    private void Label(string text, double left, double top, bool centered = false)
    {
        var label = new TextBlock { Text = text, FontSize = 13, Foreground = _text, IsHitTestVisible = false };
        if (centered)
        {
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            left -= label.DesiredSize.Width / 2;
        }

        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, top);
        _plot.Children.Add(label);
    }

    internal void BeginDrag(MsiFanCurvePointViewModel point)
    {
        if (_model?.CurvePoints.Contains(point) != true)
        {
            return;
        }

        _dragged = point;
        PositionDragReadout();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_dragged is not null)
        {
            MoveDrag(args.GetCurrentPoint(_plot).Position);
            args.Handled = true;
        }
    }

    internal void MoveDrag(Point position)
    {
        if (_dragged is null || _model is null)
        {
            return;
        }

        var index = _model.CurvePoints.IndexOf(_dragged);
        var minimumTemp = index == 0 ? 0 : _model.CurvePoints[index - 1].TemperatureC + 1;
        var maximumTemp = index == _model.CurvePoints.Count - 1 ? 100 : _model.CurvePoints[index + 1].TemperatureC - 1;
        const int minimumDuty = 0;
        const int maximumDuty = 100;

        if (minimumTemp > maximumTemp || minimumDuty > maximumDuty || !double.IsFinite(minimumTemp + maximumTemp + minimumDuty + maximumDuty))
        {
            return;
        }

        _dragged.TemperatureC = Math.Clamp(Math.Round((position.X - 55) / PlotWidth * 100, MidpointRounding.AwayFromZero), minimumTemp, maximumTemp);
        _dragged.DutyPercent = Math.Clamp(Math.Round(100 - (position.Y - 20) / PlotHeight * 100, MidpointRounding.AwayFromZero),
            minimumDuty, maximumDuty);
        PositionDragReadout();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs args) => EndDrag();

    internal void EndDrag()
    {
        HideDragReadout();
        _plot.ReleasePointerCaptures();
    }

    private void HideDragReadout()
    {
        _dragged = null;
        _dragReadout.Visibility = Visibility.Collapsed;
    }

    private void PositionDragReadout()
    {
        if (_dragged is not { } point || !double.IsFinite(point.TemperatureC) || !double.IsFinite(point.DutyPercent))
        {
            _dragReadout.Visibility = Visibility.Collapsed;
            return;
        }

        var text = $"{point.TemperatureC:0} °C  |  {point.DutyPercent:0}%";

        if (_dragText.Text != text)
        {
            _dragText.Text = text;
        }

        _dragReadout.Visibility = Visibility.Visible;
        _dragReadout.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _dragReadout.DesiredSize;
        var x = X(point.TemperatureC);
        var y = Y(point.DutyPercent);
        var left = x + 14;

        if (left + size.Width > ActualWidth)
        {
            left = x - size.Width - 14;
        }

        var top = y - size.Height - 12;
        Canvas.SetLeft(_dragReadout, Math.Round(Math.Clamp(left, 0, Math.Max(0, ActualWidth - size.Width))));
        Canvas.SetTop(_dragReadout, Math.Round(Math.Clamp(top, 0, Math.Max(0, ActualHeight - size.Height))));
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        var position = args.GetPosition(_plot);
        _model?.AddCurvePoint(Math.Round((position.X - 55) / PlotWidth * 100, MidpointRounding.AwayFromZero),
            Math.Clamp(Math.Round(100 - (position.Y - 20) / PlotHeight * 100), 0, 100));
    }

    private void PositionLive()
    {
        var visible = _snapshot is not null;
        _live.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        if (_snapshot is not { } snapshot)
        {
            return;
        }

        var x = X(snapshot.TemperatureC);
        _temperature.X1 = _temperature.X2 = x;
        _temperature.Y1 = Y(100);
        _temperature.Y2 = Y(0);
        Canvas.SetLeft(_readback, x - 7);
        Canvas.SetTop(_readback, Y(snapshot.FanDutyPercent) - 7);

        _target.Visibility = snapshot.TargetFanDutyPercent is null ? Visibility.Collapsed : Visibility.Visible;
        Canvas.SetLeft(_target, x - 5);
        Canvas.SetTop(_target, Y(snapshot.TargetFanDutyPercent ?? 0) - 5);
    }

    public void Dispose()
    {
        if (_model is not null)
        {
            _model.Edited -= Draw;
        }

        EndDrag();
        _snapshot = null;
    }
}
