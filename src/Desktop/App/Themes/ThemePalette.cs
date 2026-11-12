using Avalonia.Media;

namespace RTXLens.App.Themes;

internal readonly record struct ThemePalette(Color Primary, Color Secondary, Color Tertiary, Color Background);

internal readonly record struct PaletteSelection(ThemePreset? Preset = null, ThemePalette? CustomColors = null)
{
    public bool IsDefault => Preset is null && CustomColors is null;
}