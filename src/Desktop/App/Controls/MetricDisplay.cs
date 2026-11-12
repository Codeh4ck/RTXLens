using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace RTXLens.App.Controls;

public sealed class MetricDisplay : TemplatedControl
{
    public static readonly StyledProperty<string> LabelProperty = AvaloniaProperty.Register<MetricDisplay, string>(nameof(Label), "");
    public static readonly StyledProperty<string> ValueProperty = AvaloniaProperty.Register<MetricDisplay, string>(nameof(Value), "");
    public static readonly StyledProperty<string?> UnitProperty = AvaloniaProperty.Register<MetricDisplay, string?>(nameof(Unit));
    public static readonly StyledProperty<IImage?> IconProperty = AvaloniaProperty.Register<MetricDisplay, IImage?>(nameof(Icon));

    public static readonly StyledProperty<IBrush?> IconBackgroundProperty =
        AvaloniaProperty.Register<MetricDisplay, IBrush?>(nameof(IconBackground));

    public IBrush? IconBackground
    {
        get => GetValue(IconBackgroundProperty);
        set => SetValue(IconBackgroundProperty, value);
    }

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string? Unit
    {
        get => GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public IImage? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }
}