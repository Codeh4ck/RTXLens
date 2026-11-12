using RTXLens.Contracts.Monitoring;
using RTXLens.Presentation.Telemetry;

namespace RTXLens.Presentation.Settings;

public sealed record AppPreferences
{
    public const int DefaultWindowWidth = 1565;
    public const int DefaultWindowHeight = 1103;

    public AppTheme Theme { get; init; } = AppTheme.System;
    public string AccentColor { get; init; } = "#27D8BD";
    public double TextScale { get; init; } = 1;
    public bool Fahrenheit { get; init; }
    public bool ShowPeakHold { get; init; }
    public bool ShowHeadroom { get; init; } = true;
    public bool ShowWireViewPsuCurrent { get; init; }
    public bool ShowAsusPsuCurrent { get; init; }
    public int WireViewPsuConnector { get; init; } = 1;
    public int AsusPsuConnector { get; init; } = 1;
    public bool MinimizeToTray { get; init; } = true;
    public bool CloseToTray { get; init; }
    public bool SuppressExitConfirmation { get; init; }
    public bool StartWithWindows { get; init; }
    public bool StartMinimized { get; init; }
    public bool AutomaticallyCheckForUpdates { get; init; } = true;
    public Updates.UpdateFrequency UpdateFrequency { get; init; } = Updates.UpdateFrequency.Daily;
    public bool WarningNotifications { get; init; } = true;
    public double CoreTemperatureWarningC { get; init; } = 84;
    public double MemoryTemperatureWarningC { get; init; } = 95;
    public PinMetric PinMetric { get; init; }
    public bool ShowSeparateGpuStatusTab { get; init; } = true;
    public string StartupTabId { get; init; } = string.Empty;
    public bool RememberWindowSize { get; init; } = true;
    public bool RememberWindowPosition { get; init; } = true;
    public Dictionary<string, WindowPlacement>? WindowPlacements { get; init; }

    public double WindowWidth { get; init; } = DefaultWindowWidth;
    public double WindowHeight { get; init; } = DefaultWindowHeight;
    public int WindowLayoutVersion { get; init; } = 1;
#if DEBUG
    public string ServiceAddress { get; init; } = $"http://localhost:{ServiceEndpoint.DebugPort}/";
#else
    public string ServiceAddress { get; init; } = $"http://localhost:{ServiceEndpoint.LivePort}/";
#endif

    public void Validate()
    {
        if (StartupTabId is null || StartupTabId.Length > 128 || !Enum.IsDefined(UpdateFrequency) || WindowLayoutVersion < 1 || !Enum.IsDefined(Theme) || !Enum.IsDefined(PinMetric) || TextScale is < 0.85 or > 1.5 || !double.IsFinite(TextScale)
            || WireViewPsuConnector is < 1 or > 2 || AsusPsuConnector is < 1 or > 2
            || !double.IsFinite(CoreTemperatureWarningC) || CoreTemperatureWarningC is < 30 or > 120
            || !double.IsFinite(MemoryTemperatureWarningC) || MemoryTemperatureWarningC is < 30 or > 120
            || WindowPlacements?.Any(entry => entry.Value is null || !entry.Value.IsValid) == true
            || !double.IsFinite(WindowWidth) || !double.IsFinite(WindowHeight) || WindowWidth is < 720 or > 7680 || WindowHeight is < 550 or > 4320
            || string.IsNullOrEmpty(AccentColor) || AccentColor.Length != 7 || AccentColor[0] != '#' || !AccentColor.AsSpan(1).ToString().All(Uri.IsHexDigit)
            || !Uri.TryCreate(ServiceAddress, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(address.UserInfo) || !string.IsNullOrEmpty(address.Query) || !string.IsNullOrEmpty(address.Fragment))
        {
            throw new ArgumentException("Invalid appearance, window size or service address.");
        }
    }
}
