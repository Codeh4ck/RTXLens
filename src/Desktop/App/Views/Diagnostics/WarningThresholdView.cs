using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Contracts.Warnings;
using RTXLens.WarningUI;
using static RTXLens.App.Views.Shell.DesktopControls;
using static RTXLens.App.Views.Diagnostics.WarningVisuals;
using static RTXLens.Presentation.Telemetry.GpuStatusViewModel;

namespace RTXLens.App.Views.Diagnostics;

internal static class WarningThresholdView
{
    public static Control? Create(WarningIncident incident, PopupWarning warning, bool fahrenheit)
    {
        var rows = new StackPanel { Spacing = 8 };
        foreach (var check in incident.Thresholds.Where(check => check.Title != "Imbalance"))
        {
            var duration = check.DurationMs is { } milliseconds ? $"For {Seconds(milliseconds)} s" : null;
            var comparison = check.IsMinimum ? "≤" : "≥";
            var limit = $"{comparison} {FormatValue(check.Limit, check.Unit, fahrenheit)}";
            rows.Children.Add(Row(check.Title, IconFor(check.Unit), duration, Result(limit, null), check.Detected == true));
        }

        if (rows.Children.Count == 0 && incident.Thresholds.Length == 0
            && !warning.Title.Equals("Current imbalance", StringComparison.OrdinalIgnoreCase))
        {
            rows.Children.Add(LegacyThreshold(warning, fahrenheit));
        }

        return rows.Children.Count > 0 ? DashboardSection("Warning thresholds", "boost-limit", rows) : null;
    }

    public static Control? CreateImbalance(IEnumerable<SensorReading> connector, WarningThreshold[] thresholds, PopupWarning? warning)
    {
        if (thresholds.FirstOrDefault(check => check.Title == "Imbalance") is { } check)
        {
            var rules = new List<string> { $"Threshold ≥ {FormatValue(check.Limit, check.Unit, false)}" };
            if (check.MinimumCurrent is { } amps)
            {
                rules.Add($"Peak current ≥ {Format(amps, "A", 2)}");
            }

            if (check.DurationMs is { } duration)
            {
                rules.Add($"{Seconds(duration)} s");
            }

            return Row("Imbalance", "imbalance", string.Join(" · ", rules),
                Result(FormatValue(check.Measured, check.Unit, false), check.OutsideLimit), check.Detected == true);
        }

        var reported = warning?.Title.Equals("Current imbalance", StringComparison.OrdinalIgnoreCase) == true;
        var spread = Value(SensorMetric.ImbalancePercentage) ?? (reported
            ? Parse(warning!.Readings.FirstOrDefault(r => r.Label == "Difference")?.Value, "%") : null);
        var flag = Value(SensorMetric.ImbalanceDetected);
        if (!reported && spread is null && flag is null)
        {
            return null;
        }

        var triggered = reported || flag > 0;
        return Row("Imbalance", "imbalance", "Threshold not recorded",
            Result(Format(spread, "%", 1), triggered ? true : null), triggered);

        double? Value(SensorMetric metric) => connector.FirstOrDefault(reading => reading.Identity.Metric == metric
            && reading is { Quality: ReadingQuality.Valid, Value: { } value } && double.IsFinite(value))?.Value;
    }

    private static Control LegacyThreshold(PopupWarning warning, bool fahrenheit)
    {
        var temperatureLimit = Parse(warning.Readings.FirstOrDefault(r => r.Label == "Threshold")?.Value, " °C");
        if (temperatureLimit is { } limit)
        {
            return Row(warning.Title, "vtemp", null, Result($"≥ {FormatTemperature(limit, fahrenheit)}", null), triggered: true);
        }

        var voltage = warning.Title.Contains("voltage", StringComparison.OrdinalIgnoreCase);
        var current = warning.Title.Contains("overcurrent", StringComparison.OrdinalIgnoreCase);
        return Row(warning.Title, voltage ? "voltage" : current ? "overcurrent" : "boost-limit",
            null, Result("Not recorded", null), triggered: true);
    }

    private static Control Row(string title, string icon, string? detail, TextBlock value, bool triggered)
    {
        var description = new StackPanel { Spacing = 4, Children = { IconLabel(title, icon, 22) } };
        if (detail is not null)
        {
            description.Children.Add(Caption(detail));
        }

        value.VerticalAlignment = VerticalAlignment.Center;
        value.TextAlignment = TextAlignment.Right;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12, Children = { description, value } };
        Grid.SetColumn(value, 1);
        return new Border
        {
            Padding = new Thickness(12, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            [!Border.BackgroundProperty] = new DynamicResourceExtension(triggered ? "ErrorSurfaceBrush" : "InsetBrush"),
            [!Border.BorderBrushProperty] = new DynamicResourceExtension(triggered ? "ErrorBrush" : "PanelBorderBrush"),
            Child = row
        };
    }

    private static TextBlock Result(string value, bool? outside) => new()
    {
        Text = value, FontSize = 17, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
        [!TextBlock.ForegroundProperty] = new DynamicResourceExtension(outside switch
        {
            true => "ErrorBrush",
            false => "SuccessBrush",
            _ => "TextBrush"
        })
    };

    private static string Seconds(int milliseconds) => (milliseconds / 1000d).ToString("0.###", CultureInfo.CurrentCulture);

    private static string FormatValue(double? number, SensorUnit unit, bool fahrenheit) => unit switch
    {
        SensorUnit.Celsius => FormatTemperature(number, fahrenheit),
        SensorUnit.Amperes => Format(number, "A", 2),
        SensorUnit.Volts => Format(number, "V", 2),
        SensorUnit.Percent => Format(number, "%", 1),
        _ => "Unavailable"
    };

    private static string IconFor(SensorUnit unit) => unit switch
    {
        SensorUnit.Celsius => "vtemp",
        SensorUnit.Amperes => "overcurrent",
        SensorUnit.Volts => "voltage",
        SensorUnit.Percent => "imbalance",
        _ => "boost-limit"
    };

    private static double? Parse(string? value, string suffix) =>
        value?.EndsWith(suffix, StringComparison.Ordinal) == true
        && double.TryParse(value[..^suffix.Length].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
        && double.IsFinite(number) ? number : null;
}
