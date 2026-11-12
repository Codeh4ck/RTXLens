using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace RTXLens.App.Controls;

public sealed class SectionPanel : HeaderedContentControl
{
    public static readonly StyledProperty<double> ContentMaxWidthProperty =
        AvaloniaProperty.Register<SectionPanel, double>(nameof(ContentMaxWidth), double.PositiveInfinity);

    public static readonly StyledProperty<IImage?> HeaderIconProperty =
        AvaloniaProperty.Register<SectionPanel, IImage?>(nameof(HeaderIcon));

    public static readonly StyledProperty<object?> HeaderActionsProperty =
        AvaloniaProperty.Register<SectionPanel, object?>(nameof(HeaderActions));

    public double ContentMaxWidth
    {
        get => GetValue(ContentMaxWidthProperty);
        set => SetValue(ContentMaxWidthProperty, value);
    }

    public IImage? HeaderIcon
    {
        get => GetValue(HeaderIconProperty);
        set => SetValue(HeaderIconProperty, value);
    }

    public object? HeaderActions
    {
        get => GetValue(HeaderActionsProperty);
        set => SetValue(HeaderActionsProperty, value);
    }
}