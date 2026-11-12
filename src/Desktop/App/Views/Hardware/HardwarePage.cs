using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Shapes;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Styling;
using Avalonia.Layout;
using Avalonia.Media;
using RTXLens.App.Controls;
using RTXLens.App.ViewModels.Shell;
using RTXLens.Plugins.WireView.Contracts.Input;
using RTXLens.Presentation.Telemetry;
using static RTXLens.App.Views.Shell.DesktopControls;

namespace RTXLens.App.Views.Hardware;

internal sealed class HardwarePage : UserControl
{
    public HardwarePage(HardwarePanelViewModel panel, ShellViewModel shell)
    {
        Margin = new Thickness(0, 0, -PageScrollbarGutter, 0);
        var body = new StackPanel { Spacing = 14, Margin = new Thickness(0, 12, PageScrollbarGutter, 12) };
        body.Children.Add(CreateHeader(panel, shell));

        if (panel.IsMsi)
        {
            body.Children.Add(AdaptiveCards(width => width >= 1100 ? 4 : 2,
                Readout("PSU TEMPERATURE", "vtemp", panel.Msi.Temperature, nameof(MetricViewModel.Value), tile: true),
                Readout("PSU FAN", "vfan", panel.Msi.FanSpeed, nameof(MetricViewModel.Value), tile: true),
                Readout("FAN MODE", "vfan", panel.Msi.FanMode, nameof(MetricViewModel.Value), tile: true),
                Readout("REQUESTED FAN DUTY", "vfan", panel.Msi.FanDuty, nameof(MetricViewModel.Value), tile: true)));

            body.Children.Add(Columns([.. panel.Msi.Connectors.Select(CreateMsiConnector)]));
        }
        else
        {
            if (panel.IsWireView)
            {
                body.Children.Add(new ItemsControl
                {
                    ItemsSource = panel.Faults,
                    ItemTemplate = new FuncDataTemplate<MetricViewModel>((fault, _) => fault is null ? null : new Border
                    {
                        Padding = new Thickness(0, 0, 0, 8),
                        Child = Readout(fault)
                    })
                });
            }

            if (panel.IsAsus)
            {
                var warning = Message(tone: "error");
                BindTo(warning, ContentProperty, panel, nameof(panel.WarningText));
                BindTo(warning, IsVisibleProperty, panel, nameof(panel.HasWarning));
                body.Children.Add(warning);
            }

            body.Children.Add(CreateConnectorSummary(panel, shell.Dashboard.GpuStatus));
            body.Children.Add(CreateDetails(panel, shell));
        }

        Content = Scroll(body);
    }

    private static Grid CreateHeader(HardwarePanelViewModel panel, ShellViewModel shell)
    {
        var device = Caption();
        if (panel.IsNvidia)
        {
            BindTo(device, TextBlock.TextProperty, shell.Dashboard.GpuStatus, nameof(GpuStatusViewModel.DeviceText));
        }
        else
        {
            BindTo(device, TextBlock.TextProperty, panel, nameof(panel.DeviceText));
        }
        var iconName = panel.IsMsi ? "msi" : panel.IsWireView ? "tg" : panel.IsAsus ? "asus" : "vcore";
        var icon = new Border
        {
            Classes = { "icon-surface" },
            Child = new Image { Source = Icon(iconName), Width = 32, Height = 32 }
        };

        var title = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = panel.IsMsi ? "MSI MPG Ai1300TS / Ai1600TS" : panel.Name, Classes = { "heading" } },
                device
            }
        };

        var actions = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 8,
            LineSpacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        var restart = ActionButton("Restart plugin", "refresh", () => shell.RestartPluginCommand.Execute(panel.Id));
        restart.Classes.Add("desktop-action");
        BindTo(restart, IsVisibleProperty, panel, nameof(panel.CanRestart));
        BindTo(restart, IsEnabledProperty, shell, "!IsBusy");
        actions.Children.Add(restart);

        if (panel.IsMsi)
        {
            var reset = ActionButton("Reset peaks", "refresh", panel.Msi.ResetPeaks);
            reset.Classes.Add("desktop-action");
            BindTo(reset, IsVisibleProperty, panel.Msi, nameof(panel.Msi.ShowPeakHold));
            actions.Children.Add(reset);
            if (OperatingSystem.IsWindows())
            {
                var history = ActionButton("Safeguard+ History", "timer", () =>
                {
                    if (OperatingSystem.IsWindows() && TopLevel.GetTopLevel(actions) is Window owner)
                    {
                        shell.OpenSafeguardHistory(owner);
                    }
                });
                history.Classes.Add("desktop-action");
                Watch(history, shell, () => history.IsVisible = shell is { HasSafeguardHistory: true, IsSimulation: false });
                actions.Children.Add(history);
            }
        }

        if (panel.IsWireView)
        {
            var settings = ActionButton("Device settings", "settings", () =>
            {
                if (TopLevel.GetTopLevel(actions) is Window owner)
                {
                    var open = owner.OwnedWindows.OfType<WireViewDeviceWindow>().FirstOrDefault();
                    if (open is null)
                    {
                        var window = new WireViewDeviceWindow(shell, panel);
                        shell.TrackToolWindow(window, "wireview-settings", owner, besideOwner: true);
                        window.Show(owner);
                    }
                    else
                    {
                        open.Activate();
                    }
                }
            });
            settings.Classes.Add("desktop-action");
            Watch(settings, panel, UpdateSettings);
            Watch(settings, shell, UpdateSettings, nameof(shell.CanOpenWireViewSettings));
            actions.Children.Add(settings);

            void UpdateSettings() => settings.IsVisible = panel is { CanReadDevice: true, IsReporting: true }
                                                          && shell.CanOpenWireViewSettings;

            var clear = ActionButton("Clear faults", "chart-clear", async void () =>
            {
                if (TopLevel.GetTopLevel(actions) is not Window owner)
                {
                    return;
                }

                var statusFlags = new CheckBox { Content = "Clear current fault-status flags", IsChecked = true };
                var logFlags = new CheckBox { Content = "Clear logged fault flags" };
                var content = new StackPanel
                {
                    Spacing = 8,
                    Children = { Caption("An ongoing physical fault can be reported again."), statusFlags, logFlags }
                };

                if (!await ConfirmAsync(owner, "Clear WireView Pro II faults", content, "Clear selected flags")
                    || statusFlags.IsChecked != true && logFlags.IsChecked != true)
                {
                    return;
                }

                var request = new ClearFaultsRequest
                {
                    FaultStatusMask = statusFlags.IsChecked == true ? ushort.MaxValue : (ushort)0,
                    FaultLogMask = logFlags.IsChecked == true ? ushort.MaxValue : (ushort)0
                };
                await shell.RunAsync(() => shell.Api.ClearWireViewFaultsAsync(request, shell.StopToken), "Faults cleared.");
            });
            clear.Classes.Add("desktop-action");
            BindTo(clear, IsVisibleProperty, panel, nameof(panel.ShowClearFaults));
            actions.Children.Add(clear);
        }

        var status = Message(tone: "success", badge: true);
        BindTo(status, ContentProperty, panel, nameof(panel.State));
        BindTo(status, ToolTip.TipProperty, panel, nameof(panel.Message));
        Watch(status, panel, () =>
        {
            status.Classes.Set("success", panel.Tone == StatusTone.Positive);
            status.Classes.Set("warning", panel.Tone == StatusTone.Caution);
            status.Classes.Set("error", panel.Tone == StatusTone.Negative);
        });
        actions.Children.Add(status);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        var identity = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 12,
            Children = { icon, title }
        };
        Grid.SetColumn(title, 1);
        header.Children.Add(identity);

        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        var breakpoint = panel.IsWireView ? 1000 : panel.IsMsi ? 960 : 720;
        header.SizeChanged += (_, args) =>
        {
            var stacked = args.NewSize.Width < breakpoint;
            header.ColumnSpacing = stacked ? 0 : 12;
            header.RowSpacing = stacked ? 10 : 0;
            Grid.SetRow(actions, stacked ? 1 : 0);
            Grid.SetColumn(actions, stacked ? 0 : 1);
            Grid.SetColumnSpan(actions, stacked ? 2 : 1);
        };

        return header;
    }

    private static Grid CreateConnectorSummary(HardwarePanelViewModel panel, GpuStatusViewModel gpu)
    {
        object source = panel.IsNvidia ? gpu : panel;
        var power = Readout("CONNECTOR POWER", "pin", source, nameof(panel.PowerText), tile: true);
        var current = Readout("TOTAL CURRENT", "overcurrent", source, nameof(panel.CurrentText), tile: true);
        var voltage = Readout("VOLTAGE", "voltage", source, nameof(panel.VoltageText), tile: true);

        return panel.IsWireView
            ? AdaptiveCards(width => width >= 1160 ? 6 : width >= 650 ? 3 : 2,
                Readout(panel.CableRating, tile: true), power, current, voltage,
                Readout(panel.FaultStatus, tile: true), Readout(panel.FaultLogStatus, tile: true))
            : AdaptiveCards(width => width >= 650 ? 3 : 2, power, current, voltage);
    }

    private static Grid CreateDetails(HardwarePanelViewModel panel, ShellViewModel shell)
    {
        var gpu = shell.Dashboard.GpuStatus;
        var status = GpuStatusPanel.Create(gpu, panel.IsWireView ? panel : null);

        if (panel.IsNvidia)
        {
            var diagnostics = Columns(status, GpuStatusPanel.CreatePower(gpu.Diagnostics), GpuStatusPanel.CreatePcie(gpu.Diagnostics));
            StackAtNarrowWidth(diagnostics, 1240);
            return diagnostics;
        }

        var pinPanel = CreateGpuPins(panel, shell);
        BindTo(pinPanel, IsVisibleProperty, panel, nameof(panel.HasPins));

        var details = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("1.55*,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            Children = { pinPanel, status }
        };

        details.SizeChanged += (_, _) => ArrangeDetails();
        Watch(details, panel, ArrangeDetails, nameof(panel.HasPins));
        return details;

        void ArrangeDetails()
        {
            var wide = details.Bounds.Width >= 960 && panel.HasPins;
            details.ColumnDefinitions[0].Width = new GridLength(wide ? 1.55 : 1, GridUnitType.Star);
            details.ColumnDefinitions[1].Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            details.ColumnSpacing = wide ? 14 : 0;
            details.RowSpacing = wide || !panel.HasPins ? 0 : 14;
            Grid.SetRow(status, wide || !panel.HasPins ? 0 : 1);
            Grid.SetColumn(status, wide ? 1 : 0);
        }
    }

    private static SectionPanel CreateMsiConnector(MsiConnectorViewModel connector)
    {
        var metrics = Columns(
            PinStatistic("CURRENT SPREAD (Δ)", connector, nameof(connector.Spread), 18, "TertiaryBrush", "Max − min current"),
            PinStatistic("SUM OF PIN CURRENTS", connector, nameof(connector.Total), 18));
        metrics.ColumnSpacing = 8;

        var section = DashboardSection(connector.Name, "pin",
            new StackPanel { Spacing = 12, Children = { metrics, CreatePinChart(connector.Pins) } });
        section.ContentMaxWidth = 680;
        return section;
    }

    private static SectionPanel CreateGpuPins(HardwarePanelViewModel panel, ShellViewModel shell)
    {
        var metrics = Columns(
            PinStatistic("CURRENT SPREAD (Δ)", panel, nameof(panel.CurrentSpreadText), 18, "TertiaryBrush", "Max − min current"),
            PinStatistic("HIGHEST PIN", panel, nameof(panel.HighestCurrentText), 14),
            PinStatistic("LOWEST PIN", panel, nameof(panel.LowestCurrentText), 14));
        metrics.ColumnSpacing = 8;

        var headroom = Caption("CURRENT HEADROOM TO LIMIT");
        headroom.FontSize = 10;
        headroom.HorizontalAlignment = HorizontalAlignment.Center;
        BindTo(headroom, IsVisibleProperty, panel, nameof(panel.ShowHeadroom));

        var comparison = Caption();
        comparison.FontSize = 10;
        comparison.HorizontalAlignment = HorizontalAlignment.Center;
        BindTo(comparison, TextBlock.TextProperty, panel, nameof(panel.PsuComparisonLabel));
        BindTo(comparison, IsVisibleProperty, panel, nameof(panel.ShowPsuComparison));

        var reset = ActionButton("Reset peaks", "refresh", () =>
        {
            foreach (var pin in panel.Pins)
            {
                pin.ResetPeak();
            }
        });
        reset.Classes.Add("desktop-action");
        reset.HorizontalAlignment = HorizontalAlignment.Right;
        BindTo(reset, IsVisibleProperty, panel, nameof(panel.ShowPeakHold));

        var choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var (metric, label) in new[] { (PinMetric.Current, "A"), (PinMetric.Power, "W"), (PinMetric.Voltage, "V") })
        {
            var option = new RadioButton
            {
                GroupName = "PinMetric",
                Classes = { "pin-metric" },
                Content = label,
                IsChecked = shell.Preferences.PinMetric == metric,
                Padding = new Thickness(12, 7)
            };
            if (Application.Current?.TryFindResource("NavigationTabTheme", out var theme) == true)
            {
                option.Theme = (ControlTheme?)theme;
            }

            Avalonia.Automation.AutomationProperties.SetName(option, $"Show pin {metric.ToString().ToLowerInvariant()}");
            option.Click += (_, _) => shell.SetPinMetric(metric);
            choices.Children.Add(option);
        }

        var section = DashboardSection("Pin distribution", "pin", new StackPanel
        {
            Spacing = 12,
            Children = { metrics, headroom, CreatePinChart(panel.Pins), comparison, reset }
        });
        section.ContentMaxWidth = 680;
        section.HeaderActions = choices;
        return section;
    }

    private static Border PinStatistic(string label, object source, string path, double fontSize,
        string? borderBrush = null, string? detail = null)
    {
        var title = Caption(label);
        title.FontSize = 10;
        var value = new ReadingText { FontSize = fontSize, FontWeight = FontWeight.SemiBold };
        value[!ReadingText.UnitBrushProperty] = new DynamicResourceExtension("MutedTextBrush");
        BindTo(value, ReadingText.ReadingProperty, source, path);

        var body = new StackPanel { Spacing = 4, Children = { title, value } };
        if (detail is not null)
        {
            var caption = Caption(detail);
            caption.FontSize = 10;
            body.Children.Add(caption);
        }

        var card = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(8), Child = body };
        card[!Border.BackgroundProperty] = new DynamicResourceExtension("InsetBrush");
        if (borderBrush is not null)
        {
            card.BorderThickness = new Thickness(1);
            card[!Border.BorderBrushProperty] = new DynamicResourceExtension(borderBrush);
        }

        if (borderBrush == "TertiaryBrush")
        {
            title[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(borderBrush);
            title.FontWeight = FontWeight.SemiBold;
        }

        return card;
    }

    private static Grid CreatePinChart(IReadOnlyList<PinViewModel> pins)
    {
        var bars = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("48,*,*,*,*,*,*"),
            ColumnSpacing = 4,
            Name = "PinDistributionPlot"
        };
        var ticks = new Canvas { Height = PinBar.PlotHeight, VerticalAlignment = VerticalAlignment.Top, Name = "PinScale" };
        bars.Children.Add(ticks);

        (PinChartScale Scale, bool Headroom)? axisState = null;

        for (var index = 0; index < pins.Count; index++)
        {
            var pin = pins[index];
            var bar = new PinBar { Label = pin.Label };

            bar.AttachedToVisualTree += (_, _) =>
            {
                Update();
                pin.PropertyChanged += OnPinChanged;
            };
            bar.DetachedFromVisualTree += (_, _) => pin.PropertyChanged -= OnPinChanged;

            Grid.SetColumn(bar, index + 1);
            bars.Children.Add(bar);
            continue;

            void Update()
            {
                bar.Reading = pin.DisplayValue;
                bar.FillFraction = pin.BarHeight / 190;
                bar.BaselineFraction = pin.Scale.Unit == "V" ? 1 - pin.BaselineTop / 190 : null;
                bar.PeakFraction = pin.ShowPeak ? 1 - pin.PeakTop / 190 : null;
                bar.Headroom = pin.ShowHeadroom ? pin.Headroom : null;
                bar.Comparison = pin.ShowPsuCurrent ? pin.PsuCurrentText : null;
                bar.Classes.Set("error", pin.IsWarning);
                bar.Classes.Set("overflow", pin.IsOverflow);
                bar.Classes.Set("headroom-warning", pin.HeadroomTone == StatusTone.Caution);
                bar.Classes.Set("headroom-error", pin.HeadroomTone == StatusTone.Negative);
            }

            void OnPinChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName is null or "" or nameof(pin.DisplayValue) or nameof(pin.PsuCurrentText) or nameof(pin.ShowPsuCurrent)
                    or nameof(pin.ShowHeadroom) or nameof(pin.PeakTop))
                {
                    Update();
                }

                if (pin == pins[0] && args.PropertyName is null or "" or nameof(pin.Scale) or nameof(pin.ShowHeadroom))
                {
                    RenderScale();
                }
            }
        }

        RenderScale();
        bars.AttachedToVisualTree += (_, _) => RenderScale();
        return bars;

        void RenderScale()
        {
            var pin = pins[0];
            var scale = pin.Scale;
            var nextState = (scale, pin.ShowHeadroom);
            if (axisState == nextState)
            {
                return;
            }

            axisState = nextState;
            ticks.Margin = new Thickness(0, PinBar.ReadingHeight + (pin.ShowHeadroom ? PinBar.HeadroomHeight : 0), 0, 0);
            ticks.Children.Clear();
            var axis = new Rectangle { Width = 1, Height = PinBar.PlotHeight };
            axis[!Shape.FillProperty] = new DynamicResourceExtension("MutedTextBrush");
            Canvas.SetLeft(axis, 42);
            ticks.Children.Add(axis);

            for (var index = 0; index <= scale.Intervals; index++)
            {
                var fraction = (double)index / scale.Intervals;
                var amount = scale.Maximum - fraction * (scale.Maximum - scale.Minimum);
                var offset = fraction * PinBar.PlotHeight;

                var label = Caption(amount.ToString("0.##", CultureInfo.CurrentCulture) + scale.Unit);
                label.Width = 38;
                label.FontSize = 10;
                label.TextAlignment = TextAlignment.Right;
                Canvas.SetTop(label, offset - 7);

                var tick = new Rectangle { Width = 5, Height = 1 };
                tick[!Shape.FillProperty] = new DynamicResourceExtension("MutedTextBrush");
                Canvas.SetLeft(tick, 42);
                Canvas.SetTop(tick, offset);

                ticks.Children.Add(label);
                ticks.Children.Add(tick);
            }
        }
    }

    internal static MetricDisplay Readout(MetricViewModel metric, bool tile = false)
    {
        var display = Readout(metric.Label, metric.IconName, metric, nameof(metric.Value), tile);
        BindTo(display, MetricDisplay.LabelProperty, metric, nameof(metric.Label));
        Watch(display, metric, () => display.Classes.Set("error", metric.Tone == StatusTone.Negative), nameof(metric.Tone));

        return display;
    }

    internal static MetricDisplay Readout(string label, string? icon, object source, string path, bool tile = false)
    {
        var display = new MetricDisplay { Label = label, Icon = icon is null ? null : Icon(icon) };
        display.Classes.Set("tile", tile);
        BindTo(display, MetricDisplay.ValueProperty, source, path);
        return display;
    }
}
