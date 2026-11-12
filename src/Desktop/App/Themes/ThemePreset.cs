using Avalonia.Media;

namespace RTXLens.App.Themes;

internal sealed record ThemePreset(string Id, string Name, ThemePalette Dark, ThemePalette Light)
{
    public static IReadOnlyList<ThemePreset> All { get; } =
    [
        new("ultraviolet", "Ultraviolet",
            ParsePalette("#BF9AFF", "#DAB9FF", "#F3ACED", "#110D1D"),
            ParsePalette("#6743A9", "#8056B7", "#9C387D", "#F6F1FC")),
        new("emerald-circuit", "Emerald Circuit",
            ParsePalette("#61E995", "#9ADDB6", "#C1E77E", "#0B1611"),
            ParsePalette("#12713C", "#2E7955", "#4B7317", "#EFF8F2")),
        new("crimson-pulse", "Crimson Pulse",
            ParsePalette("#FF7888", "#F4A5AE", "#FFBCA1", "#1B0E15"),
            ParsePalette("#A92D46", "#963A5E", "#973F2F", "#FCF0F3")),
        new("amber-signal", "Amber Signal",
            ParsePalette("#FFD26C", "#F3C196", "#EBE18B", "#1A130A"),
            ParsePalette("#775007", "#87501D", "#6B6313", "#FBF5E8")),
        new("cobalt-surge", "Cobalt Surge",
            ParsePalette("#78B2FF", "#9BCDFF", "#A8B6FF", "#0A1220"),
            ParsePalette("#235DAA", "#246A91", "#4B50A8", "#EEF4FC"))
    ];

    private static ThemePalette ParsePalette(string primary, string secondary, string tertiary, string background) =>
        new(Color.Parse(primary), Color.Parse(secondary), Color.Parse(tertiary), Color.Parse(background));
}
