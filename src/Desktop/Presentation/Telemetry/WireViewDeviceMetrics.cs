using RTXLens.Presentation.Settings;
using System.Globalization;
using RTXLens.Plugins.WireView.Contracts.Common;

namespace RTXLens.Presentation.Telemetry;

internal static class WireViewDeviceMetrics
{
    public static MetricViewModel[] Create(DeviceConfig config) =>
    [
        new("Version", Number(config.Version), "firmware"),
        new("Friendly name", Text(config.FriendlyName), "tag"),
        new("Backlight duty", Number(config.BacklightDuty, "%"), "brightness"),
        new("Logging interval", Number(config.LoggingInterval), "timer"),
        new("Averaging", Text(config.Averaging), "average"),
        new("Fan mode", Text(config.FanMode), DesktopIcons.Fan),
        new("Fan temperature source", Text(config.FanTempSource), DesktopIcons.Temperature),
        new("Fan duty min / max", Pair(config.FanDutyMin, config.FanDutyMax, "%"), DesktopIcons.Fan),
        new("Fan temperature min / max", Pair(config.FanTempMinC, config.FanTempMaxC, "°C"), DesktopIcons.Temperature),
        new("Overtemperature threshold", Number(config.TsFaultThresholdC, "°C"), DesktopIcons.Temperature),
        new("Total overcurrent threshold", Number(config.OcpFaultThresholdA, "A"), "overcurrent"),
        new("Per-pin overcurrent threshold", Number(config.WireOcpFaultThresholdA, "A"), "pin"),
        new("Overpower threshold", Number(config.OppFaultThresholdW, "W"), "power"),
        new("Current imbalance threshold", Number(config.CurrentImbalanceThreshold), "imbalance"),
        new("Current imbalance minimum load", Number(config.CurrentImbalanceMinLoad), "overcurrent"),
        new("Shutdown wait time", Number(config.ShutdownWaitTimeS, "s"), "timer"),
        new("Fault display", Flags(config.FaultDisplayEnable), "display"),
        new("Fault buzzer", Flags(config.FaultBuzzerEnable), "buzzer"),
        new("Fault soft power", Flags(config.FaultSoftPowerEnable), "power-button"),
        new("Fault hard power", Flags(config.FaultHardPowerEnable), "power-cut")
    ];

    private static string Number(double value, string? unit = null) => value.ToString("0.##", CultureInfo.CurrentCulture) + (unit is null ? string.Empty : " " + unit);
    private static string Pair(double first, double second, string unit) => Number(first, unit) + " / " + Number(second, unit);
    private static string Text(string? value) => string.IsNullOrWhiteSpace(value) ? "Unavailable" : value;
    private static string Flags(ushort value) => value == 0 ? "Disabled" : FaultDescriptions.Describe(value);
}
