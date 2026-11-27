using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using RTXLens.App.Controls;
using RTXLens.App.Views.Hardware;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Contracts.Warnings;
using RTXLens.Presentation.Telemetry;
using RTXLens.Plugins.Msi.Contracts;
using RTXLens.Plugins.WireView.Contracts;
using RTXLens.WarningUI;
using static RTXLens.App.Views.Shell.DesktopControls;
using static RTXLens.App.Views.Diagnostics.WarningVisuals;
using static RTXLens.Presentation.Telemetry.GpuStatusViewModel;

namespace RTXLens.App.Views.Diagnostics;

internal sealed class WarningSnapshotView : StackPanel
{
    public WarningSnapshotView(WarningIncident incident, PopupWarning warning, bool fahrenheit)
    {
        Spacing = 12;
        if (incident.CapturedAt is { } captured)
        {
            Children.Add(Caption($"Readings captured {captured.LocalDateTime:G}"));
        }

        var pins = incident.Readings.Where(r => r.Identity.Component is SensorComponent.GpuPin or SensorComponent.PsuPin)
            .GroupBy(r => (r.Identity.Component, r.Identity.ConnectorId)).ToArray();
        foreach (var group in pins)
        {
            var (component, connector) = group.Key;
            var label = component == SensorComponent.GpuPin ? "GPU pins" : "PSU pins";
            if (connector is not null)
            {
                label += $" · Connector {connector}";
            }

            var connectorReadings = incident.Readings.Where(r => r.Identity.Component ==
                                                                 (component == SensorComponent.GpuPin ? SensorComponent.GpuConnector : SensorComponent.PsuConnector)
                                                                 && r.Identity.ConnectorId == connector);
            var matchesIncident = pins.Length == 1 || (incident.Connector is { } target && connector == target.ToString(CultureInfo.InvariantCulture));
            var checks = matchesIncident ? incident.Thresholds : [];
            var trigger = matchesIncident ? warning : null;
            var content = new StackPanel { Spacing = 10 };
            if (WarningThresholdView.CreateImbalance(connectorReadings, checks, trigger) is { } imbalance)
            {
                content.Children.Add(imbalance);
            }

            content.Children.Add(PinCards(group, connectorReadings, trigger, checks));
            Children.Add(DashboardSection(label, "gpu-connector", content));
        }

        if (pins.Length == 0 && WarningThresholdView.CreateImbalance(
            incident.Readings.Where(reading => reading.Identity.Component is SensorComponent.GpuConnector or SensorComponent.PsuConnector),
            incident.Thresholds, warning) is { } imbalanceOnly)
        {
            Children.Add(imbalanceOnly);
        }

        if (incident.Kind == "warning" && WarningThresholdView.Create(incident, warning, fahrenheit) is { } thresholds)
        {
            Children.Add(thresholds);
        }

        if (incident.Readings.Length == 0 && incident.GpuReadings.Length == 0)
        {
            Children.Add(Section("Captured readings", Caption("No sensor readings were saved for this incident.")));
            return;
        }

        var deviceReadings = incident.Readings.Where(r => r.Identity.Component is not (SensorComponent.GpuPin or SensorComponent.PsuPin)
            && !r.Identity.IsGpuStatus() && !r.Identity.IsGpuDiagnostic());
        var wireViewReadings = deviceReadings.Where(IsWireViewDeviceReading).ToArray();
        if (wireViewReadings.Length > 0)
        {
            Children.Add(CreateWireViewDevicePanel(wireViewReadings, fahrenheit));
        }

        foreach (var group in deviceReadings.Where(r => !IsWireViewDeviceReading(r)).GroupBy(r => r.Identity.Component))
        {
            var cards = group.Where(r => r.Identity.Metric is not (SensorMetric.ImbalanceDetected or SensorMetric.ImbalancePercentage))
                .OrderBy(r => r.Identity.Metric)
                .Select(r => MetricCard(r.Identity, r, fahrenheit))
                .ToArray();
            if (cards.Length > 0)
            {
                Children.Add(DashboardSection(Heading(group.Key), GroupIcon(group.Key), Cards(cards)));
            }
        }

        AddGpuPanels(incident, fahrenheit);
    }

    private static bool IsWireViewDeviceReading(SensorReading reading) =>
        reading.Identity.PluginId == WireViewIdentity.Id && reading.Identity is
            { Component: SensorComponent.MonitorBoard or SensorComponent.ExternalProbe, Metric: SensorMetric.Temperature, Channel: 1 or 2 }
            or { Component: SensorComponent.MonitorBoard, Metric: SensorMetric.FaultStatus or SensorMetric.FaultLog };

    private static Control CreateWireViewDevicePanel(SensorReading[] readings, bool fahrenheit)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*"), RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnSpacing = 12, RowSpacing = 12
        };
        Add(0, 0, SensorComponent.MonitorBoard, SensorMetric.Temperature, 1);
        Add(0, 1, SensorComponent.ExternalProbe, SensorMetric.Temperature, 1);
        Add(0, 2, SensorComponent.MonitorBoard, SensorMetric.FaultStatus);
        Add(1, 0, SensorComponent.MonitorBoard, SensorMetric.Temperature, 2);
        Add(1, 1, SensorComponent.ExternalProbe, SensorMetric.Temperature, 2);
        Add(1, 2, SensorComponent.MonitorBoard, SensorMetric.FaultLog);

        return DashboardSection("Device readings", "power", grid);

        void Add(int row, int column, SensorComponent component, SensorMetric metric, int? channel = null)
        {
            var identity = new SensorIdentity(WireViewIdentity.Id, readings[0].Identity.DeviceId, component, metric, channel);
            var reading = readings.FirstOrDefault(r => r.Identity == identity);
            var card = MetricCard(identity, reading, fahrenheit);
            Grid.SetRow(card, row);
            Grid.SetColumn(card, column);
            grid.Children.Add(card);
        }
    }

    private void AddGpuPanels(WarningIncident incident, bool fahrenheit)
    {
        var gpus = incident.GpuReadings.Concat(incident.Readings)
            .Where(r => (r.Identity.IsGpuStatus() || r.Identity.IsGpuDiagnostic()) && Available(r).HasValue)
            .GroupBy(r => r.Identity.DeviceId).OrderBy(group => group.Key, StringComparer.Ordinal).ToArray();
        if (gpus.Length == 0 && incident.Source is WireViewIdentity.Id or MsiIdentity.Id)
        {
            Children.Add(Section("GPU statistics", Caption("No GPU readings were saved for this incident.")));
        }

        foreach (var group in gpus)
        {
            if (gpus.Length > 1)
            {
                Children.Add(Caption($"GPU · {group.Key}"));
            }

            var readings = group.GroupBy(r => (r.Identity.Component, r.Identity.Metric, r.Identity.Channel))
                .Select(metric => metric.OrderByDescending(r => r.Identity.PluginId == incident.Source)
                    .ThenByDescending(r => r.ObservedAt).First()).ToArray();
            var gpu = new GpuStatusViewModel();
            gpu.ApplyCapturedReadings(readings, fahrenheit);

            if (readings.Any(r => r.Identity.IsGpuStatus()))
            {
                Children.Add(GpuStatusPanel.Create(gpu, wireView: null, isDedicatedTab: true, captured: true));
            }

            var diagnostics = new List<Control>(2);
            if (readings.Any(r => r.Identity.Component is SensorComponent.GpuPowerLimits or SensorComponent.GpuPerformance))
            {
                diagnostics.Add(GpuStatusPanel.CreatePower(gpu.Diagnostics));
            }

            if (readings.Any(r => r.Identity.Component == SensorComponent.GpuPcie))
            {
                diagnostics.Add(GpuStatusPanel.CreatePcie(gpu.Diagnostics));
            }

            if (diagnostics.Count == 1)
            {
                Children.Add(diagnostics[0]);
            }
            else if (diagnostics.Count == 2)
            {
                Children.Add(AdaptiveCards(width => width >= 680 ? 2 : 1, diagnostics.ToArray()));
            }
        }
    }

    private static Control PinCards(IEnumerable<SensorReading> readings, IEnumerable<SensorReading> connector,
        PopupWarning? warning, WarningThreshold[] thresholds)
    {
        var rows = readings.GroupBy(r => r.Identity.Channel).OrderBy(g => g.Key).ToArray();
        var reportedPins = warning is null ? [] : warning.Readings.Select(r => PinNumber(r.Label)).OfType<int>().ToHashSet();
        var overcurrentCheck = thresholds.FirstOrDefault(check => check.Unit == SensorUnit.Amperes);
        var imbalanceCheck = thresholds.FirstOrDefault(check => check.Title == "Imbalance");
        var voltageChecks = thresholds.Where(check => check.Unit == SensorUnit.Volts).ToArray();
        var imbalanceFlag = Value(connector, SensorMetric.ImbalanceDetected);
        var detailedImbalance = warning?.Title.Equals("Current imbalance", StringComparison.OrdinalIgnoreCase) == true;
        var imbalancePins = detailedImbalance && reportedPins.Count > 0 ? reportedPins
            : imbalanceCheck?.OffendingPins.Select(reading => reading.Pin).ToHashSet() ?? [];
        if ((imbalanceCheck?.OutsideLimit ?? imbalanceFlag > 0) && imbalancePins.Count == 0)
        {
            var currents = rows.Select(row => (Pin: row.Key, Current: Value(row, SensorMetric.Current)))
                .Where(p => p.Pin.HasValue && p.Current.HasValue).ToArray();
            if (currents.Length >= 2)
            {
                var low = currents.MinBy(p => p.Current);
                var high = currents.MaxBy(p => p.Current);
                if (high.Current > low.Current)
                {
                    imbalancePins.Add(low.Pin!.Value);
                    imbalancePins.Add(high.Pin!.Value);
                }
            }
        }

        return Cards(rows.Select(row =>
        {
            var pin = row.Key;
            var named = pin.HasValue && reportedPins.Contains(pin.Value);
            var amperage = Value(row, SensorMetric.Current);
            var voltage = Value(row, SensorMetric.Voltage);
            var power = Value(row, SensorMetric.Power);
            var overcurrentFlag = Value(row, SensorMetric.OvercurrentDetected);
            var overcurrent = overcurrentCheck is { } currentLimit
                ? amperage + 0.000001 >= currentLimit.Limit || currentLimit.OffendingPins.Any(reading => reading.Pin == pin)
                : overcurrentFlag > 0;
            overcurrent |= named && warning?.Title.Contains("overcurrent", StringComparison.OrdinalIgnoreCase) == true;

            var imbalance = pin.HasValue && imbalancePins.Contains(pin.Value);
            var lowVoltage = VoltageExceeded(minimum: true) || (named && warning?.Title == "Low pin voltage");
            var highVoltage = VoltageExceeded(minimum: false) || (named && warning?.Title == "High pin voltage");

            var issues = new List<string>();
            if (overcurrent)
            {
                issues.Add("Overcurrent");
            }

            if (imbalance)
            {
                issues.Add("Imbalance");
            }

            if (lowVoltage)
            {
                issues.Add("Low pin voltage");
            }

            if (highVoltage)
            {
                issues.Add("High pin voltage");
            }

            var hasOffense = issues.Count > 0;
            var known = overcurrentFlag.HasValue || (amperage.HasValue && overcurrentCheck is not null)
                || (voltage is > 0 && voltageChecks.Length > 0);
            var state = hasOffense ? Badge(string.Join(" + ", issues), "error")
                : known ? Badge("No issue", "success") : Badge("Status unavailable", "info");
            var current = MetricValue(Format(amperage, "A", 2), 21,
                ValueBrush(amperage, overcurrent || imbalance));
            var card = new StackPanel
            {
                Spacing = 6, Children =
                {
                    new TextBlock { Text = pin is { } index ? $"Pin {index}" : "Pin", FontWeight = FontWeight.SemiBold },
                    current
                }
            };

            if (voltage.HasValue || power.HasValue)
            {
                var electrical = new WrapPanel { ItemSpacing = 12, LineSpacing = 4 };
                if (voltage.HasValue)
                {
                    electrical.Children.Add(Field("Voltage", Format(voltage, "V", 2), foreground: ValueBrush(voltage, lowVoltage || highVoltage)));
                }

                if (power.HasValue)
                {
                    electrical.Children.Add(Field("Power", Format(power, "W", 1), foreground: ValueBrush(power, crossed: false)));
                }

                card.Children.Add(electrical);
            }

            state.ContentTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((label, _) => DialogText(label));
            card.Children.Add(state);
            return Surface(card, "InsetBrush", hasOffense ? "ErrorBrush" : known ? "SuccessBrush" : "PanelBorderBrush");

            bool VoltageExceeded(bool minimum) => voltageChecks.Any(check => check.IsMinimum == minimum
                && (check.OffendingPins.Any(reading => reading.Pin == pin)
                    || (voltage is > 0 && (minimum ? voltage <= check.Limit : voltage >= check.Limit))));
        }));
    }

    private static string ValueBrush(double? value, bool crossed) =>
        crossed ? "ErrorBrush" : value.HasValue ? "SuccessBrush" : "MutedTextBrush";

    private static Grid Cards(IEnumerable<Control> cards) =>
        AdaptiveCards(width => width >= 610 ? 3 : width >= 340 ? 2 : 1, cards.ToArray());

    private static Border MetricCard(SensorIdentity identity, SensorReading? reading, bool fahrenheit)
    {
        var isFault = identity is { PluginId: WireViewIdentity.Id, Component: SensorComponent.MonitorBoard,
            Metric: SensorMetric.FaultStatus or SensorMetric.FaultLog };
        var mask = isFault ? FaultMask(reading) : null;
        var value = isFault
            ? mask is { } flags ? FaultDescriptions.Describe(flags) : "Unavailable"
            : reading is not null ? Display(reading, fahrenheit) : "Unavailable";
        var accent = mask switch
        {
            > 0 => "ErrorBrush",
            0 => "SuccessBrush",
            _ when value == "Unavailable" => "MutedTextBrush",
            _ => "PrimaryBrush"
        };
        return Surface(new StackPanel
        {
            Spacing = 6,
            Children = { IconLabel(Label(identity), GroupIcon(identity.Metric), 20, caption: true), MetricValue(value, accent: accent) }
        }, "InsetBrush", mask > 0 ? "ErrorBrush" : "PanelBorderBrush");
    }

    private static Border Surface(Control content, string background, string border) => new()
    {
        Padding = new Thickness(12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
        [!Border.BackgroundProperty] = new DynamicResourceExtension(background),
        [!Border.BorderBrushProperty] = new DynamicResourceExtension(border),
        Child = content
    };

    private static ReadingText MetricValue(string value, double fontSize = 18, string accent = "PrimaryBrush") => new()
    {
        Reading = value, FontSize = fontSize, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
        [!TextBlock.ForegroundProperty] = new DynamicResourceExtension(accent),
        [!ReadingText.UnitBrushProperty] = new DynamicResourceExtension("MutedTextBrush")
    };

    private static double? Value(IEnumerable<SensorReading> readings, SensorMetric metric) =>
        readings.Where(r => r.Identity.Metric == metric).Select(Available).FirstOrDefault(value => value.HasValue);

    private static double? Available(SensorReading reading) =>
        reading is { Quality: ReadingQuality.Valid, Value: { } value } && double.IsFinite(value) ? value : null;

    private static ushort? FaultMask(SensorReading? reading) =>
        reading is not null && Available(reading) is >= 0 and <= ushort.MaxValue and var mask
            && mask == Math.Truncate(mask) ? (ushort)mask : null;

    private static string Display(SensorReading reading, bool fahrenheit)
    {
        var value = Available(reading);
        if (!value.HasValue)
        {
            return "Unavailable";
        }

        return reading.Unit switch
        {
            SensorUnit.Amperes => Format(value, "A", 2),
            SensorUnit.Volts when reading.Identity.Component == SensorComponent.GpuCore => Format(value * 1000, "mV", 1),
            SensorUnit.Volts => Format(value, "V", 2),
            SensorUnit.Watts => Format(value, "W", 1),
            SensorUnit.Celsius => FormatTemperature(value, fahrenheit),
            SensorUnit.Megahertz => Format(value, "MHz", 0),
            SensorUnit.Rpm => Format(value, "RPM", 0),
            SensorUnit.Percent => Format(value, "%", reading.Identity.Metric == SensorMetric.ImbalancePercentage ? 1 : 0),
            SensorUnit.Megabytes => Format(value / 1024, "GB", 2),
            SensorUnit.Boolean when reading.Identity.Metric == SensorMetric.ImbalanceDetected => value == 0 ? "No issue" : "Imbalance",
            SensorUnit.Boolean => value == 0 ? "No" : "Yes",
            SensorUnit.Bitmask => $"0x{(ulong)value.Value:X}",
            _ => value.Value.ToString("0.##", CultureInfo.CurrentCulture)
        };
    }

    private static string Heading(SensorComponent component) => component switch
    {
        SensorComponent.GpuConnector => "GPU connector",
        SensorComponent.PsuConnector => "PSU connector",
        SensorComponent.Psu => "Power supply",
        SensorComponent.MonitorBoard => "Device readings",
        SensorComponent.ExternalProbe => "External temperatures",
        _ => Humanize(component.ToString())
    };

    private static string GroupIcon(SensorComponent component) => component switch
    {
        SensorComponent.GpuConnector or SensorComponent.PsuConnector => "gpu-connector",
        SensorComponent.ExternalProbe => "vtemp",
        _ => "power"
    };

    private static string? GroupIcon(SensorMetric metric) => metric switch
    {
        SensorMetric.Current or SensorMetric.FaultStatus or SensorMetric.FaultLog => "overcurrent",
        SensorMetric.Power or SensorMetric.PowerCapability => "power",
        SensorMetric.Voltage => "voltage",
        SensorMetric.Temperature => "vtemp",
        SensorMetric.FanDuty or SensorMetric.FanSpeed => "vfan",
        _ => null
    };

    private static string Label(SensorIdentity identity)
    {
        var metric = Humanize(identity.Metric.ToString());
        return (identity.Component, identity.Channel, identity.ConnectorId) switch
        {
            (SensorComponent.PsuConnector, _, { } connector) => $"Connector {connector} · {metric}",
            (SensorComponent.GpuMemoryChip, { } chip, _) => $"VRAM chip {chip}",
            (SensorComponent.MonitorBoard, 1, _) => "Board In temperature",
            (SensorComponent.MonitorBoard, 2, _) => "Board Out temperature",
            (SensorComponent.ExternalProbe, { } probe, _) => $"Ext. {probe} temperature",
            _ => metric
        };
    }

    private static string Humanize(string name) => Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ");
}
