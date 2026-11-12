using Avalonia.Media;

namespace RTXLens.App.Themes;

internal sealed record AppearanceSettings(bool LightTheme, string? Primary, string? Secondary, string? Tertiary, string? Background)
{
    public string? PresetId { get; init; }

    public ThemePalette? ReadPalette()
    {
        if (Primary is null || Secondary is null || Tertiary is null || Background is null)
        {
            return null;
        }

        return Color.TryParse(Primary, out var primary)
            && Color.TryParse(Secondary, out var secondary)
            && Color.TryParse(Tertiary, out var tertiary)
            && Color.TryParse(Background, out var background)
                ? new ThemePalette(primary, secondary, tertiary, background)
                : null;
    }

    public static AppearanceSettings FromPalette(bool lightTheme, ThemePalette? palette) => new(lightTheme,
        palette?.Primary.ToString(), palette?.Secondary.ToString(), palette?.Tertiary.ToString(), palette?.Background.ToString());
}