using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace RTXLens.App.Controls;

public sealed partial class ReadingText : TextBlock
{
    public static readonly StyledProperty<string?> ReadingProperty =
        AvaloniaProperty.Register<ReadingText, string?>(nameof(Reading));

    public static readonly StyledProperty<IBrush?> UnitBrushProperty =
        AvaloniaProperty.Register<ReadingText, IBrush?>(nameof(UnitBrush));

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public string? Reading
    {
        get => GetValue(ReadingProperty);
        set => SetValue(ReadingProperty, value);
    }

    public IBrush? UnitBrush
    {
        get => GetValue(UnitBrushProperty);
        set => SetValue(UnitBrushProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ReadingProperty || change.Property == UnitBrushProperty)
        {
            UpdateReading();
        }
    }

    private void UpdateReading()
    {
        var text = Reading ?? string.Empty;
        var inlines = Inlines!;
        var brush = UnitBrush;
        inlines.Clear();

        var offset = 0;
        if (brush is not null)
        {
            foreach (var match in Units().EnumerateMatches(text))
            {
                if (match.Index > offset)
                {
                    inlines.Add(new Run(text[offset..match.Index]));
                }

                inlines.Add(new Run(text.Substring(match.Index, match.Length)) { Foreground = brush });
                offset = match.Index + match.Length;
            }
        }

        if (offset < text.Length)
        {
            inlines.Add(new Run(text[offset..]));
        }
    }

    [GeneratedRegex(@"(?<=RPM) \(\d+%\)|(?<=\d)[ \u00a0\u202f]*"
        + @"(?:GT/s per lane|GiB/s|MiB/s|KiB/s|B/s|MHz|RPM|°[CF]|GB|mV|W|A|V|%)(?!\p{L})")]
    private static partial Regex Units();
}
