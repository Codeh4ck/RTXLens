using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace RTXLens.App.Controls;

public sealed class PinTrack : Control
{
    private const double CornerRadius = 4;

    public static readonly StyledProperty<double> FillFractionProperty = AvaloniaProperty.Register<PinTrack, double>(nameof(FillFraction));
    public static readonly StyledProperty<double?> PeakFractionProperty =
        AvaloniaProperty.Register<PinTrack, double?>(nameof(PeakFraction));
    public static readonly StyledProperty<double?> BaselineFractionProperty =
        AvaloniaProperty.Register<PinTrack, double?>(nameof(BaselineFraction));
    public static readonly StyledProperty<IBrush?> TrackBrushProperty = AvaloniaProperty.Register<PinTrack, IBrush?>(nameof(TrackBrush));
    public static readonly StyledProperty<IBrush?> FillBrushProperty = AvaloniaProperty.Register<PinTrack, IBrush?>(nameof(FillBrush));
    public static readonly StyledProperty<IBrush?> PeakBrushProperty = AvaloniaProperty.Register<PinTrack, IBrush?>(nameof(PeakBrush));
    public static readonly StyledProperty<IBrush?> BaselineBrushProperty =
        AvaloniaProperty.Register<PinTrack, IBrush?>(nameof(BaselineBrush));

    static PinTrack()
    {
        AffectsRender<PinTrack>(FillFractionProperty, PeakFractionProperty, BaselineFractionProperty,
            TrackBrushProperty, FillBrushProperty, PeakBrushProperty, BaselineBrushProperty);
    }

    public double FillFraction
    {
        get => GetValue(FillFractionProperty);
        set => SetValue(FillFractionProperty, value);
    }

    public double? PeakFraction
    {
        get => GetValue(PeakFractionProperty);
        set => SetValue(PeakFractionProperty, value);
    }

    public double? BaselineFraction
    {
        get => GetValue(BaselineFractionProperty);
        set => SetValue(BaselineFractionProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? FillBrush
    {
        get => GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public IBrush? PeakBrush
    {
        get => GetValue(PeakBrushProperty);
        set => SetValue(PeakBrushProperty, value);
    }

    public IBrush? BaselineBrush
    {
        get => GetValue(BaselineBrushProperty);
        set => SetValue(BaselineBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        using var clip = context.PushClip(new RoundedRect(bounds, CornerRadius));
        if (TrackBrush is { } track)
        {
            context.FillRectangle(track, bounds);
        }

        if (FillBrush is { } fill && double.IsFinite(FillFraction) && FillFraction > 0)
        {
            var height = bounds.Height * Math.Min(FillFraction, 1);
            context.FillRectangle(fill, new Rect(0, bounds.Height - height, bounds.Width, height));
        }

        DrawMarker(context, bounds, BaselineFraction, BaselineBrush, 1);
        DrawMarker(context, bounds, PeakFraction, PeakBrush, 2);
    }

    private static void DrawMarker(DrawingContext context, Rect bounds, double? fraction, IBrush? brush, double thickness)
    {
        if (fraction is not { } value || !double.IsFinite(value) || brush is null)
        {
            return;
        }

        var height = Math.Min(thickness, bounds.Height);
        var top = Math.Clamp(bounds.Height * (1 - value) - height / 2, 0, bounds.Height - height);
        context.FillRectangle(brush, new Rect(0, top, bounds.Width, height));
    }
}