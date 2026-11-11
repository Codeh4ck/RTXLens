using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace RTXLens.App.Controls;

public static partial class MetricText
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
        "Value", typeof(string), typeof(MetricText), new PropertyMetadata(string.Empty, OnReadingChanged));

    public static readonly DependencyProperty UnitForegroundProperty = DependencyProperty.RegisterAttached(
        "UnitForeground", typeof(Brush), typeof(MetricText), new PropertyMetadata(null, OnReadingChanged));

    public static string GetValue(DependencyObject element) => (string)element.GetValue(ValueProperty);
    public static void SetValue(DependencyObject element, string value) => element.SetValue(ValueProperty, value);

    public static Brush? GetUnitForeground(DependencyObject element) => (Brush?)element.GetValue(UnitForegroundProperty);
    public static void SetUnitForeground(DependencyObject element, Brush? value) => element.SetValue(UnitForegroundProperty, value);

    private static void OnReadingChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not TextBlock textBlock)
        {
            return;
        }

        var value = GetValue(textBlock);
        var unitBrush = GetUnitForeground(textBlock);
        if (unitBrush is null || string.IsNullOrEmpty(value))
        {
            textBlock.Text = value ?? string.Empty;
            return;
        }

        textBlock.Inlines.Clear();
        var offset = 0;

        foreach (var unit in ReadingUnits().EnumerateMatches(value))
        {
            if (unit.Index > offset)
            {
                textBlock.Inlines.Add(new Run { Text = value[offset..unit.Index] });
            }

            textBlock.Inlines.Add(new Run { Text = value.Substring(unit.Index, unit.Length), Foreground = unitBrush });
            offset = unit.Index + unit.Length;
        }

        if (offset < value.Length)
        {
            textBlock.Inlines.Add(new Run { Text = value[offset..] });
        }
    }

    [GeneratedRegex(@"(?<=RPM) \(\d+%\)|(?<=\d)[ \u00a0\u202f]*(?:GT/s per lane|GiB/s|MiB/s|KiB/s|B/s|MHz|RPM|°[CF]|GB|mV|W|A|V|%)(?!\p{L})")]
    private static partial Regex ReadingUnits();
}
