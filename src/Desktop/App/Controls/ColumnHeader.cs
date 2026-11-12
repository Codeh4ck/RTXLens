using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace RTXLens.App.Controls;

public sealed class ColumnHeader : TemplatedControl
{
    public static readonly StyledProperty<string> LabelProperty = AvaloniaProperty.Register<ColumnHeader, string>(nameof(Label), "");
    public static readonly StyledProperty<string?> SortIndicatorProperty =
        AvaloniaProperty.Register<ColumnHeader, string?>(nameof(SortIndicator));
    public static readonly StyledProperty<ICommand?> SortCommandProperty =
        AvaloniaProperty.Register<ColumnHeader, ICommand?>(nameof(SortCommand));

    private Thumb? _resizeGrip;

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string? SortIndicator
    {
        get => GetValue(SortIndicatorProperty);
        set => SetValue(SortIndicatorProperty, value);
    }

    public ICommand? SortCommand
    {
        get => GetValue(SortCommandProperty);
        set => SetValue(SortCommandProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        if (_resizeGrip is not null)
        {
            _resizeGrip.DragDelta -= OnResize;
            _resizeGrip.KeyDown -= OnResizeKeyDown;
        }

        _resizeGrip = e.NameScope.Find("PART_ResizeGrip") as Thumb;

        if (_resizeGrip is not null)
        {
            _resizeGrip.DragDelta += OnResize;
            _resizeGrip.KeyDown += OnResizeKeyDown;
        }
    }

    private void OnResize(object? sender, VectorEventArgs e) => ResizeBy(e.Vector.X);

    private void OnResizeKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:
                ResizeBy(-8);
                break;
            case Key.Right:
                ResizeBy(8);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void ResizeBy(double offset)
    {
        var width = double.IsNaN(Width) ? Bounds.Width : Width;
        SetCurrentValue(WidthProperty, Math.Clamp(width + offset, MinWidth, MaxWidth));
    }
}