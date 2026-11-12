using Avalonia;
using Avalonia.Controls.Primitives;

namespace RTXLens.App.Controls;

public sealed class PinBar : TemplatedControl
{
    public const double PlotHeight = 158;
    public const double ReadingHeight = 22;
    public const double HeadroomHeight = 34;

    public static readonly StyledProperty<string> LabelProperty = AvaloniaProperty.Register<PinBar, string>(nameof(Label), "");
    public static readonly StyledProperty<string> ReadingProperty = AvaloniaProperty.Register<PinBar, string>(nameof(Reading), "");
    public static readonly StyledProperty<string?> HeadroomProperty = AvaloniaProperty.Register<PinBar, string?>(nameof(Headroom));
    public static readonly StyledProperty<string?> ComparisonProperty = AvaloniaProperty.Register<PinBar, string?>(nameof(Comparison));

    public static readonly StyledProperty<double> FillFractionProperty = PinTrack.FillFractionProperty.AddOwner<PinBar>();
    public static readonly StyledProperty<double?> PeakFractionProperty = PinTrack.PeakFractionProperty.AddOwner<PinBar>();
    public static readonly StyledProperty<double?> BaselineFractionProperty = PinTrack.BaselineFractionProperty.AddOwner<PinBar>();

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Reading
    {
        get => GetValue(ReadingProperty);
        set => SetValue(ReadingProperty, value);
    }

    public string? Headroom
    {
        get => GetValue(HeadroomProperty);
        set => SetValue(HeadroomProperty, value);
    }

    public string? Comparison
    {
        get => GetValue(ComparisonProperty);
        set => SetValue(ComparisonProperty, value);
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
}