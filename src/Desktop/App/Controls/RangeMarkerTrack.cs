using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace RTXLens.App.Controls;

public sealed class RangeMarkerTrack : Control
{
    public static readonly StyledProperty<double?> ActivePositionProperty =
        AvaloniaProperty.Register<RangeMarkerTrack, double?>(nameof(ActivePosition));

    public static readonly StyledProperty<double?> ReferencePositionProperty =
        AvaloniaProperty.Register<RangeMarkerTrack, double?>(nameof(ReferencePosition));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<RangeMarkerTrack, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> ActiveBrushProperty =
        AvaloniaProperty.Register<RangeMarkerTrack, IBrush?>(nameof(ActiveBrush));

    public static readonly StyledProperty<IBrush?> ReferenceBrushProperty =
        AvaloniaProperty.Register<RangeMarkerTrack, IBrush?>(nameof(ReferenceBrush));

    static RangeMarkerTrack()
    {
        AffectsRender<RangeMarkerTrack>(ActivePositionProperty, ReferencePositionProperty,
            TrackBrushProperty, ActiveBrushProperty, ReferenceBrushProperty);
    }

    public double? ActivePosition
    {
        get => GetValue(ActivePositionProperty);
        set => SetValue(ActivePositionProperty, value);
    }

    public double? ReferencePosition
    {
        get => GetValue(ReferencePositionProperty);
        set => SetValue(ReferencePositionProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? ActiveBrush
    {
        get => GetValue(ActiveBrushProperty);
        set => SetValue(ActiveBrushProperty, value);
    }

    public IBrush? ReferenceBrush
    {
        get => GetValue(ReferenceBrushProperty);
        set => SetValue(ReferenceBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        const double inset = 5;
        var width = Bounds.Width - 2 * inset;
        if (width <= 0)
        {
            return;
        }

        var middle = Bounds.Height / 2;
        context.DrawRectangle(TrackBrush, null, new Rect(inset, middle - 1, width, 2), 1, 1);

        if (ActivePosition is { } active && double.IsFinite(active))
        {
            var x = inset + width * Math.Clamp(active, 0, 1);
            context.DrawRectangle(ActiveBrush, null, new Rect(x - 1.5, middle - 7, 3, 9), 1.5, 1.5);
        }

        if (ReferencePosition is { } reference && double.IsFinite(reference) && ReferenceBrush is { } brush)
        {
            var x = inset + width * Math.Clamp(reference, 0, 1);
            context.DrawEllipse(null, new Pen(brush, 1.5), new Point(x, middle + 3), 3, 3);
        }
    }
}
