using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;

namespace RTXLens.App.Themes;

internal sealed class PaletteResources(Application application)
{
    private ResourceDictionary? _overrides;
    private ThemePalette? _palette;

    public ThemePalette ReadDefaults(ThemeVariant variant) => new(
        ReadColor("DefaultPrimaryColor", variant), ReadColor("DefaultSecondaryColor", variant),
        ReadColor("DefaultTertiaryColor", variant), ReadColor("DefaultBackgroundColor", variant));

    public void Apply(ThemePalette? palette)
    {
        if (_palette == palette)
        {
            return;
        }

        _palette = palette;

        var dictionaries = application.Resources.MergedDictionaries;
        if (palette is not { } colors)
        {
            if (_overrides is not null)
            {
                dictionaries.Remove(_overrides);
                _overrides = null;
            }

            return;
        }

        var next = CreateOverrides(colors);
        if (_overrides is null)
        {
            dictionaries.Add(next);
        }
        else
        {
            dictionaries[dictionaries.IndexOf(_overrides)] = next;
        }

        _overrides = next;
    }

    private Color ReadColor(string key, ThemeVariant variant) =>
        application.TryGetResource(key, variant, out var value) && value is Color color
            ? color
            : throw new InvalidOperationException($"Missing appearance resource '{key}' for '{variant}'.");

    private static ResourceDictionary CreateOverrides(ThemePalette palette)
    {
        var background = Color.FromRgb(palette.Background.R, palette.Background.G, palette.Background.B);
        var foreground = Contrast(Colors.White, background) >= Contrast(Colors.Black, background) ? Colors.White : Colors.Black;
        var panel = Blend(background, foreground, 0.05);
        var inset = Blend(background, foreground, 0.02);
        var track = Blend(background, foreground, 0.12);
        var accent = Readable(palette.Primary, panel, 4.5);
        var onAccent = Contrast(Colors.White, accent) >= Contrast(Colors.Black, accent) ? Colors.White : Colors.Black;
        var input = Blend(inset, accent, 0.04);

        return new ResourceDictionary
        {
            ["WindowBackgroundBrush"] = Brush(background),
            ["PanelBrush"] = Brush(panel),
            ["InsetBrush"] = Brush(inset),
            ["TrackBrush"] = Brush(track),
            ["PanelBorderBrush"] = Brush(Blend(background, foreground, 0.24)),
            ["TextBrush"] = Brush(Readable(foreground, panel, 4.5)),
            ["MutedTextBrush"] = Brush(Readable(Blend(panel, foreground, 0.65), panel, 4.5)),
            ["PrimaryBrush"] = Brush(accent),
            ["SecondaryBrush"] = Brush(Readable(palette.Secondary, panel, 4.5)),
            ["TertiaryBrush"] = Brush(Readable(palette.Tertiary, panel, 4.5)),
            ["SystemAccentColor"] = accent,
            ["SystemAccentColorLight1"] = Blend(accent, onAccent, 0.07),
            ["SystemAccentColorDark1"] = Blend(accent, onAccent, 0.14),
            ["HudOnAccentColor"] = onAccent,
            ["HudInputColor"] = input,
            ["HudInputStrokeColor"] = Blend(input, accent, 0.35),
            ["HudAccentSurfaceColor"] = Blend(input, accent, 0.1),
            ["HudHoverSurfaceColor"] = Blend(input, accent, 0.16),
            ["HudPressedSurfaceColor"] = Blend(input, accent, 0.23)
        };
    }

    private static ImmutableSolidColorBrush Brush(Color color) => new(color);

    private static Color Readable(Color requested, Color background, double minimumContrast)
    {
        var color = Color.FromRgb(requested.R, requested.G, requested.B);
        var fallback = Contrast(Colors.White, background) >= Contrast(Colors.Black, background) ? Colors.White : Colors.Black;

        for (var step = 0; step < 20; step++)
        {
            var candidate = Blend(color, fallback, step / 20.0);
            if (Contrast(candidate, background) >= minimumContrast)
            {
                return candidate;
            }
        }

        return fallback;
    }

    private static Color Blend(Color start, Color end, double amount) => Color.FromRgb(
        BlendChannel(start.R, end.R, amount), BlendChannel(start.G, end.G, amount), BlendChannel(start.B, end.B, amount));

    private static byte BlendChannel(byte start, byte end, double amount) => (byte)Math.Round(start + (end - start) * amount);

    private static double Contrast(Color first, Color second)
    {
        var firstLuminance = Luminance(first);
        var secondLuminance = Luminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + 0.05) / (Math.Min(firstLuminance, secondLuminance) + 0.05);
    }

    private static double Luminance(Color color) =>
        0.2126 * Linearize(color.R) + 0.7152 * Linearize(color.G) + 0.0722 * Linearize(color.B);

    private static double Linearize(byte channel)
    {
        var value = channel / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
}