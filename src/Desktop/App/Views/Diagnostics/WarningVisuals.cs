using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using RTXLens.WarningUI;
using static RTXLens.App.Views.Shell.DesktopControls;

namespace RTXLens.App.Views.Diagnostics;

internal static class WarningVisuals
{
    private static readonly Geometry AlertGlyph = Geometry.Parse("M12 2 L23 21 H1 Z M11 8 V14 H13 V8 Z M11 16 V18 H13 V16 Z");
    private static readonly Geometry CheckGlyph = Geometry.Parse("M12 1 L22 5 V11 Q22 19 12 23 Q2 19 2 11 V5 Z M6 12 L10 16 L18 8 L16.5 6.5 L10 13 L7.5 10.5 Z");
    private static readonly Geometry HistoryGlyph = Geometry.Parse("M12 2 A10 10 0 1 1 11.99 2 Z M12 4 A8 8 0 1 0 12.01 4 Z M11 6 H13 V11.5 L16.5 14 L15.5 15.5 L11 12.5 Z");

    public static Control Glyph(string tone, double size = 34, bool history = false)
    {
        var (ink, surface) = tone switch
        {
            "error" => ("ErrorBrush", "ErrorSurfaceBrush"),
            "warning" => ("WarningBrush", "WarningSurfaceBrush"),
            "success" => ("SuccessBrush", "SuccessSurfaceBrush"),
            _ => ("IconBrush", "TrackBrush")
        };

        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(8),
            VerticalAlignment = VerticalAlignment.Center,
            [!Border.BackgroundProperty] = new DynamicResourceExtension(surface),
            Child = new PathIcon
            {
                Data = history ? HistoryGlyph : tone == "success" ? CheckGlyph : AlertGlyph,
                Width = size * 0.48, Height = size * 0.48,
                [!TemplatedControl.ForegroundProperty] = new DynamicResourceExtension(ink)
            }
        };
    }

    public static string Tone(string status) => status switch
    {
        "Active" => "error",
        "Recovering" or "Unavailable" or "Interrupted" => "warning",
        "Cleared" or "Succeeded" => "success",
        "Failed" => "error",
        _ => "info"
    };

    public static ContentControl Badge(string status, string? tone = null)
    {
        var badge = Message(status, tone ?? Tone(status), badge: true);
        badge.HorizontalAlignment = HorizontalAlignment.Left;
        badge.VerticalAlignment = VerticalAlignment.Center;
        return badge;
    }

    public static StackPanel Field(string label, string value, double fontSize = 14, string? foreground = null)
    {
        var text = DialogText(value);
        text.FontSize = fontSize;
        text.FontWeight = FontWeight.SemiBold;
        if (foreground is not null)
        {
            text[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(foreground);
        }

        return new StackPanel { Spacing = 3, Children = { Caption(label), text } };
    }

    public static Control Readings(PopupReading[] readings, bool active, bool fahrenheit = false)
    {
        var grid = AdaptiveCards(width => width >= 640 ? 6 : width >= 400 ? 4 : width >= 260 ? 3 : 2,
            readings.Select(reading => (Control)new Border
            {
                Padding = new Thickness(10, 7), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
                [!Border.BackgroundProperty] = new DynamicResourceExtension("InsetBrush"),
                [!Border.BorderBrushProperty] = new DynamicResourceExtension("PanelBorderBrush"),
                Child = Field(PinLabel(reading.Label), ReadingValue(reading.Value, fahrenheit),
                    17, active ? "ErrorBrush" : null)
            }).ToArray());
        grid.ColumnSpacing = 8;
        grid.RowSpacing = 8;
        return grid;
    }

    private static string ReadingValue(string text, bool fahrenheit)
    {
        if (text.EndsWith(" °C", StringComparison.Ordinal) && double.TryParse(text[..^3].Replace(',', '.'),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature))
        {
            return Presentation.Telemetry.GpuStatusViewModel.FormatTemperature(temperature, fahrenheit);
        }

        var separator = NumberFormatInfo.CurrentInfo.NumberDecimalSeparator;
        return text.Replace(".", separator).Replace(",", separator);
    }

    public const string InterruptedExplanation =
        "The service restarted before it recorded how this warning ended.";

    public static int? PinNumber(string label) =>
        label.StartsWith('P') && int.TryParse(label.AsSpan(1), out var pin) && pin > 0 ? pin : null;

    private static string PinLabel(string label) => PinNumber(label) is { } pin ? $"Pin {pin}" : label;

    public static Grid TableFrame(Control table)
    {
        var surface = new Border
        {
            Margin = new Thickness(1), CornerRadius = new CornerRadius(7), ClipToBounds = true, Child = table,
            [!Border.BackgroundProperty] = new DynamicResourceExtension("PanelBrush")
        };
        var outline = new Border
        {
            Classes = { "panel-frame" }, Background = Brushes.Transparent, IsHitTestVisible = false
        };
        // Keep the outline outside the rounded content clip so the stroke renders at fractional DPI.
        return new Grid { UseLayoutRounding = true, Children = { surface, outline } };
    }

    public static Border StatePanel(string title, string? detail, string tone, Control? action = null)
    {
        var content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 14,
            RowDefinitions = new RowDefinitions("Auto,Auto"), RowSpacing = action is null ? 0 : 12
        };
        var copy = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(detail))
        {
            copy.Children.Add(Caption(detail));
        }

        Grid.SetColumn(copy, 1);
        content.Children.Add(Glyph(tone, 44, history: tone == "info"));
        content.Children.Add(copy);
        if (action is not null)
        {
            action.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetColumn(action, 1);
            Grid.SetRow(action, 1);
            content.Children.Add(action);
        }
        return new Border { Classes = { "panel-frame" }, Padding = new Thickness(18), Child = content };
    }
}
