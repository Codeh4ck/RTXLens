using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using RTXLens.App.Controls;
using RTXLens.Presentation.Telemetry;
using static RTXLens.App.Views.Hardware.HardwarePage;
using static RTXLens.App.Views.Shell.DesktopControls;

namespace RTXLens.App.Views.Hardware;

internal static class GpuStatusPanel
{
    public static SectionPanel Create(GpuStatusViewModel gpu, HardwarePanelViewModel? wireView)
    {
        var device = Caption();
        BindTo(device, TextBlock.TextProperty, gpu, nameof(gpu.DeviceText));

        var unavailable = Message();
        BindTo(unavailable, ContentControl.ContentProperty, gpu, nameof(gpu.State));
        BindTo(unavailable, Visual.IsVisibleProperty, gpu, nameof(gpu.IsUnavailable));

        var readings = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Inset(ReadingRow(IconLabel("GPU usage", "boost-limit", 24), Reading(gpu, nameof(gpu.GpuUsageText))), 44),
                Inset(ReadingRow(IconLabel("Core voltage", "voltage", 24), Reading(gpu, nameof(gpu.CoreVoltageText))), 44),
                Group("Clocks", "vcore", Pair(Column("Core clock", gpu.CoreClock), Column("Memory clock", gpu.MemoryClock))),
                CreateVram(gpu),
                CreateTemperatures(gpu),
                CreateFans(gpu)
            }
        };

        if (wireView is not null)
        {
            readings.Children.Add(CompactRows(Compact(wireView.BoardTemperatures), Compact(wireView.ExternalTemperatures)));
        }

        return DashboardSection("GPU status", "vcore",
            new StackPanel { Spacing = 12, Children = { device, unavailable, readings } });
    }

    private static Border CreateVram(GpuStatusViewModel gpu)
    {
        var value = Reading(gpu.VramUsage, nameof(MetricViewModel.Value));
        BindTo(value, ToolTip.TipProperty, gpu.VramUsage, nameof(MetricViewModel.Value));
        var valueBox = new Viewbox
        {
            Child = value,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 5, Margin = new Thickness(16, 0, 0, 0) };
        progress[!TemplatedControl.ForegroundProperty] = new DynamicResourceExtension("PrimaryBrush");
        progress[!TemplatedControl.BackgroundProperty] = new DynamicResourceExtension("TrackBrush");
        BindTo(progress, ProgressBar.ValueProperty, gpu, nameof(gpu.VramUsagePercent));
        BindTo(progress, Visual.IsVisibleProperty, gpu, nameof(gpu.HasVramUsage));

        return Group("VRAM usage", "vramusage", new StackPanel { Spacing = 5, Children = { valueBox, progress } });
    }

    private static Border CreateTemperatures(GpuStatusViewModel gpu)
    {
        var hotspot = Column("Hotspot temp", gpu.HotspotTemperature);
        BindTo(hotspot, Visual.IsVisibleProperty, gpu, nameof(gpu.HasHotspotTemperature));

        var temperatures = Pair(Column("Core temp", gpu.CoreTemperature), Column("VRAM temp", gpu.MemoryTemperature));
        return Group("Temperatures", "vtemp", new StackPanel { Spacing = 5, Children = { temperatures, hotspot } });
    }

    private static Border CreateFans(GpuStatusViewModel gpu)
    {
        var fans = Pair(Column("Fan 1", gpu.Fan1), Column("Fan 2", gpu.Fan2));
        BindTo(fans.Children[1], Visual.IsVisibleProperty, gpu, nameof(gpu.HasSecondFan));

        return Group("Fans", "vfan", fans);
    }

    public static SectionPanel CreatePower(GpuDiagnosticsViewModel diagnostics)
    {
        var detailsButton = new Button
        {
            Classes = { "desktop-action" },
            Content = new Image { Source = Icon("info-action"), Width = 16, Height = 16 },
            Padding = new Thickness(7, 4),
            Flyout = new Flyout { Content = CreateBoostTable(diagnostics), FlyoutPresenterClasses = { "boost-details" } }
        };
        AutomationProperties.SetName(detailsButton, "Show boost limit details");
        ToolTip.SetTip(detailsButton, "Show boost limit details");

        var activeHeading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { IconLabel(diagnostics.ActiveBoostLimits.Label, "boost-limit", 20, caption: true), detailsButton }
        };
        var active = new StackPanel
        {
            Spacing = 5,
            Children = { activeHeading, Reading(diagnostics.ActiveBoostLimits, nameof(MetricViewModel.Value), 20) }
        };

        return DashboardSection("GPU Power Status", "power", new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Inset(active),
                CreatePowerScale(diagnostics),
                CompactRows(Compact(diagnostics.CurrentPState), Compact(diagnostics.SupportedPStates))
            }
        });
    }

    private static Control CreateBoostTable(GpuDiagnosticsViewModel diagnostics)
    {
        var list = new ItemsControl
        {
            ItemsSource = diagnostics.BoostLimits,
            ItemTemplate = new FuncDataTemplate<MetricViewModel>((metric, _) => metric is null ? null : Compact(metric, showIcon: false))
        };
        var headings = ReadingRow(Caption("Boost limit"), Caption("Time limited"));

        return Inset(new StackPanel { Spacing = 6, MinWidth = 290, Children = { headings, Divider(), list } });
    }

    private static StackPanel CreatePowerScale(GpuDiagnosticsViewModel diagnostics)
    {
        var track = new RangeMarkerTrack { Height = 20 };
        track[!RangeMarkerTrack.TrackBrushProperty] = new DynamicResourceExtension("TrackBrush");
        track[!RangeMarkerTrack.ActiveBrushProperty] = new DynamicResourceExtension("PrimaryBrush");
        track[!RangeMarkerTrack.ReferenceBrushProperty] = new DynamicResourceExtension("MutedTextBrush");

        Watch(track, diagnostics, () =>
        {
            var (enforced, defaultLimit) = diagnostics.PowerLimitPositions;
            track.ActivePosition = enforced;
            track.ReferencePosition = defaultLimit;
        }, nameof(diagnostics.PowerLimitPositions));

        var minimum = Caption();
        BindTo(minimum, TextBlock.TextProperty, diagnostics, nameof(diagnostics.MinimumPowerText));
        var maximum = Caption();
        BindTo(maximum, TextBlock.TextProperty, diagnostics, nameof(diagnostics.MaximumPowerText));

        var range = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = "GPU power limit range", FontWeight = FontWeight.SemiBold },
                track,
                ReadingRow(minimum, maximum),
                ReadingRow(PowerLegend("Enforced limit", diagnostics.EnforcedPower, active: true),
                    PowerLegend("Default limit", diagnostics.DefaultPower, active: false))
            }
        };

        BindTo(range, Visual.IsVisibleProperty, diagnostics, nameof(diagnostics.HasPowerScale));
        return range;
    }

    private static StackPanel PowerLegend(string label, MetricViewModel metric, bool active)
    {
        var marker = new Border
        {
            Width = active ? 4 : 7,
            Height = active ? 10 : 7,
            CornerRadius = new CornerRadius(active ? 2 : 4),
            VerticalAlignment = VerticalAlignment.Center
        };
        if (active)
        {
            marker[!Border.BackgroundProperty] = new DynamicResourceExtension("PrimaryBrush");
        }
        else
        {
            marker[!Border.BorderBrushProperty] = new DynamicResourceExtension("MutedTextBrush");
            marker.BorderThickness = new Thickness(1.5);
        }

        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { marker, Caption(label) } };
        var value = Reading(metric, nameof(MetricViewModel.Value), 14, accent: false);
        value.HorizontalAlignment = active ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        return new StackPanel { Spacing = 2, Children = { heading, value } };
    }

    public static SectionPanel CreatePcie(GpuDiagnosticsViewModel diagnostics)
    {
        var link = Reading(diagnostics.CurrentLink, nameof(MetricViewModel.Value), 20);
        link.TextTrimming = TextTrimming.CharacterEllipsis;
        BindTo(link, ToolTip.TipProperty, diagnostics.CurrentLink, nameof(MetricViewModel.Value));

        var speed = Inset(new StackPanel
        {
            Spacing = 6,
            Children =
            {
                IconLabel(diagnostics.CurrentLink.Label, "pcie-link", 20, caption: true),
                link,
                Divider(),
                Compact(diagnostics.MaximumLink, showIcon: false, accent: true),
                Compact(diagnostics.MaximumGpuGeneration, showIcon: false, accent: true)
            }
        });

        return DashboardSection("GPU PCIe Status", "pcie-link", new StackPanel
        {
            Spacing = 8,
            Children =
            {
                speed,
                Columns(Traffic(diagnostics.ReceiveThroughput), Traffic(diagnostics.TransmitThroughput)),
                CompactRows(Compact(diagnostics.Replays), Compact(diagnostics.ReplayRollovers))
            }
        });
    }

    private static Border Traffic(MetricViewModel metric) =>
        Inset(new StackPanel
        {
            Spacing = 5,
            Children = { IconLabel(metric.Label, metric.IconName, 20, caption: true), Reading(metric, nameof(MetricViewModel.Value), 20) }
        }, 68);

    private static Grid Compact(MetricViewModel metric, bool showIcon = true, bool accent = false)
    {
        var value = Reading(metric, nameof(MetricViewModel.Value), 15, accent);
        value.TextTrimming = TextTrimming.CharacterEllipsis;
        value.MaxWidth = 225;
        BindTo(value, ToolTip.TipProperty, metric, nameof(MetricViewModel.Value));

        var row = ReadingRow(IconLabel(metric.Label, showIcon ? metric.IconName : null, 20, caption: !showIcon), value);
        row.MinHeight = showIcon ? 30 : 0;
        return row;
    }

    private static StackPanel CompactRows(params Control[] rows)
    {
        var panel = new StackPanel { Spacing = 1 };
        panel.Children.Add(Divider(8));
        foreach (var row in rows)
        {
            panel.Children.Add(row);
        }

        return panel;
    }

    private static StackPanel Column(string label, MetricViewModel metric)
    {
        var marker = new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 7,
            Height = 7,
            VerticalAlignment = VerticalAlignment.Center
        };
        marker[!Avalonia.Controls.Shapes.Shape.FillProperty] = new DynamicResourceExtension("ErrorBrush");
        BindTo(marker, Visual.IsVisibleProperty, metric, nameof(MetricViewModel.IsWarning));
        ToolTip.SetTip(marker, "Temperature warning active");

        var title = new TextBlock { Text = label, FontSize = 14, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        var heading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 6,
            Children = { title, marker }
        };
        var value = Reading(metric, nameof(MetricViewModel.Value));
        value.TextAlignment = TextAlignment.Center;
        value.TextWrapping = TextWrapping.Wrap;

        return new StackPanel { Spacing = 3, Margin = new Thickness(5, 0), Children = { heading, value } };
    }

    private static Grid Pair(Control first, Control second)
    {
        var divided = new Border { BorderThickness = new Thickness(1, 0, 0, 0), Child = second };
        divided[!Border.BorderBrushProperty] = new DynamicResourceExtension("PanelBorderBrush");

        var pair = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Children = { first, divided } };
        Grid.SetColumn(divided, 1);
        return pair;
    }

    private static Border Group(string label, string icon, Control readings)
    {
        var heading = IconLabel(label, icon, 24);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("126,*"), ColumnSpacing = 10, Children = { heading, readings } };
        Grid.SetColumn(readings, 1);
        return Inset(grid, 68);
    }

    private static Border Inset(Control content, double minimumHeight = 0)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, minimumHeight == 44 ? 8 : 12),
            MinHeight = minimumHeight,
            Child = content
        };
        card[!Border.BackgroundProperty] = new DynamicResourceExtension("InsetBrush");
        card[!Border.BorderBrushProperty] = new DynamicResourceExtension("PanelBorderBrush");
        return card;
    }

    private static Border Divider(double gap = 0)
    {
        var rule = new Border { Height = 1, Margin = new Thickness(0, 0, 0, gap) };
        rule[!Border.BackgroundProperty] = new DynamicResourceExtension("PanelBorderBrush");
        return rule;
    }

    private static Grid ReadingRow(Control label, Control value)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { label, value }
        };
        Grid.SetColumn(value, 1);
        value.VerticalAlignment = VerticalAlignment.Center;
        return row;
    }

    private static ReadingText Reading(object source, string path, double fontSize = 18, bool accent = true)
    {
        var value = new ReadingText { FontSize = fontSize, FontWeight = FontWeight.SemiBold };
        value[!ReadingText.UnitBrushProperty] = new DynamicResourceExtension("MutedTextBrush");
        if (accent)
        {
            value[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("PrimaryBrush");
        }

        BindTo(value, ReadingText.ReadingProperty, source, path);
        return value;
    }
}
