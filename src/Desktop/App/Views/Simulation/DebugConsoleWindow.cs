using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using RTXLens.App.Controls;
using RTXLens.App.Views.Shell;
using RTXLens.Contracts.Enums;
using RTXLens.Debugging;
using RTXLens.Debugging.ViewModels;
using static RTXLens.App.Views.Configuration.ConfigurationPage;
using static RTXLens.App.Views.Shell.DesktopControls;

namespace RTXLens.App.Views.Simulation;

internal sealed class DebugConsoleWindow : LensWindow
{
    private static readonly string[] SourceModes = ["Live readings", "Override live readings", "Simulate device"];
    private static readonly string[] MetricModes = ["Use source", "Fixed", "Unavailable", "Stale"];
    private static readonly string[] PluginStates = Enum.GetNames<PluginState>();

    public DebugConsoleWindow(DebugSession session)
    {
        Title = "RTX Lens - Debug Console";
        Width = 1020;
        Height = 830;
        MinWidth = 880;
        MinHeight = 530;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var page = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        var tabs = CreateTabs(session, page);

        var layout = new Grid
        {
            Margin = new Thickness(18),
            RowDefinitions = new RowDefinitions("Auto,Auto,*"),
            RowSpacing = 12,
            Children = { CreateSessionPanel(session), tabs, page }
        };
        Grid.SetRow(tabs, 1);
        Grid.SetRow(page, 2);
        Body = layout;
    }

    private static SectionPanel CreateSessionPanel(DebugSession session)
    {
        var enable = Switch("Enable telemetry simulation", session, nameof(session.Enabled));
        var reset = ActionButton("Reset all", "refresh", session.ResetAll);
        var status = Message("Simulation active", tone: "warning", badge: true);
        ToolTip.SetTip(status, "Overrides apply to this desktop session.");
        BindTo(status, IsVisibleProperty, session, nameof(session.Enabled));
        var notice = Caption();
        notice.VerticalAlignment = VerticalAlignment.Center;
        BindTo(notice, TextBlock.TextProperty, session, nameof(session.LastNotification));

        var panel = Section("Telemetry simulation", new WrapPanel
        {
            ItemSpacing = 12,
            LineSpacing = 5,
            Children = { status, notice }
        });
        panel.Padding = new Thickness(16, 8);
        panel.HeaderActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children = { enable, reset }
        };
        return panel;
    }

    private static TabControl CreateTabs(DebugSession session, ContentControl page)
    {
        var tabs = new TabControl { Name = "SimulationPluginTabs" };
        if (Application.Current?.TryFindResource("ConfigurationTabStripTheme", out var tabTheme) == true)
        {
            tabs.Theme = (ControlTheme?)tabTheme;
        }

        var itemTheme = Application.Current?.TryFindResource("ConfigurationTabTheme", out var resource) == true
            ? resource as ControlTheme
            : null;
        foreach (var plugin in session.Plugins)
        {
            tabs.Items.Add(new TabItem { Header = PluginName(plugin), Theme = itemTheme });
        }

        var pages = new Control?[session.Plugins.Length];

        tabs.SelectionChanged += (_, args) =>
        {
            if (ReferenceEquals(args.Source, tabs))
            {
                SelectPlugin();
            }
        };
        tabs.SelectedIndex = 0;
        SelectPlugin();

        return tabs;

        void SelectPlugin()
        {
            var index = tabs.SelectedIndex;
            if (index >= 0)
            {
                page.Content = pages[index] ??= CreatePlugin(session, session.Plugins[index]);
            }
        }
    }

    private static Control CreatePlugin(DebugSession session, DebugPluginViewModel plugin)
    {
        var setup = CreateSourcePanel(session, plugin);
        var metrics = CreateMetricTableLayout(plugin);
        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            RowSpacing = 12,
            Children = { setup, metrics }
        };
        Grid.SetRow(metrics, 1);
        return layout;
    }

    private static Control CreateSourcePanel(DebugSession session, DebugPluginViewModel plugin)
    {
        var mode = new ComboBox { ItemsSource = SourceModes, Width = 210 };
        AutomationProperties.SetName(mode, "Reading source");
        BindTo(mode, SelectingItemsControl.SelectedIndexProperty, plugin, nameof(plugin.ModeIndex), BindingMode.TwoWay);

        var reset = IconButton("Reset plugin", "refresh", () => session.ResetPlugin(plugin.Id));
        var sourceLabel = Caption("Reading source");
        sourceLabel.VerticalAlignment = VerticalAlignment.Center;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { sourceLabel, mode, reset } };
        var header = Header(PluginName(plugin));

        var state = new ComboBox { ItemsSource = PluginStates };
        BindTo(state, SelectingItemsControl.SelectedIndexProperty, plugin, nameof(plugin.StateIndex), BindingMode.TwoWay);
        var device = new TextBox { MaxLength = 128 };
        BindTo(device, TextBox.TextProperty, plugin, nameof(plugin.DeviceId), BindingMode.TwoWay);
        var installed = Switch("Installed", plugin, nameof(plugin.Installed));
        var enabled = Switch("Enabled", plugin, nameof(plugin.Enabled));

        var switches = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Bottom,
            Height = 36,
            Spacing = 12,
            Children = { installed, enabled }
        };
        var details = new Grid
        {
            Margin = new Thickness(16, 0, 16, 12),
            ColumnDefinitions = new ColumnDefinitions("*,*,Auto"),
            ColumnSpacing = 12,
            Children = { Field("Device ID", device), Field("Plugin state", state), switches }
        };
        Grid.SetColumn(details.Children[1], 1);
        Grid.SetColumn(switches, 2);
        BindTo(details, IsVisibleProperty, plugin, nameof(plugin.IsSimulated));

        var top = new Grid
        {
            Margin = new Thickness(16, 10),
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children = { header, actions }
        };
        Grid.SetColumn(actions, 1);

        return new Border
        {
            Classes = { "panel-frame" },
            Child = new StackPanel { Children = { top, details } }
        };
    }

    private static Grid CreateMetricTableLayout(DebugPluginViewModel plugin)
    {
        var metrics = new ListBox
        {
            Name = "SimulationMetrics",
            Classes = { "event-rows" },
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            // A recycled container may temporarily have no row.
            ItemTemplate = new FuncDataTemplate<DebugMetricViewModel>((metric, _) => metric is null ? null : CreateMetric(metric))
        };
        AutomationProperties.SetName(metrics, "Telemetry metrics");
        ScrollViewer.SetVerticalScrollBarVisibility(metrics, ScrollBarVisibility.Auto);

        var header = new Border
        {
            Classes = { "event-header" },
            CornerRadius = new CornerRadius(7, 7, 0, 0),
            Child = MetricColumns(Header("Metric"), Header("Live"), Header("Effective"), Header("Mode"), Header("Override"))
        };
        var table = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Children = { header, metrics } };
        Grid.SetRow(metrics, 1);

        var frame = new Border
        {
            Classes = { "panel-frame" },
            VerticalAlignment = VerticalAlignment.Top,
            ClipToBounds = true,
            Child = table
        };
        Grid.SetColumn(frame, 1);

        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 12,
            Children = { CreateCategoryTabs(plugin, metrics), frame }
        };
    }

    private static TextBlock Header(string title) => new()
    {
        Text = title,
        FontWeight = FontWeight.SemiBold,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static TabControl CreateCategoryTabs(DebugPluginViewModel plugin, ListBox metrics)
    {
        var app = Application.Current!;
        var itemTheme = (ControlTheme)app.FindResource("ConfigurationTabTheme")!;
        var tabs = new TabControl
        {
            Name = "SimulationMetricCategories",
            TabStripPlacement = Dock.Left,
            Width = 145,
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Spacing = 4 }),
            Theme = (ControlTheme)app.FindResource("VerticalConfigurationTabStripTheme")!
        };
        AutomationProperties.SetName(tabs, "Metric categories");

        tabs.SelectionChanged += (_, args) =>
        {
            if (ReferenceEquals(args.Source, tabs) && tabs.SelectedItem is TabItem { Tag: DebugMetricViewModel[] rows })
            {
                metrics.ItemsSource = rows;
                metrics.ScrollIntoView(0);
            }
        };
        tabs.ItemsSource = plugin.Metrics.GroupBy(MetricCategory)
            .OrderByDescending(group => group.First().Definition is { Component: SensorComponent.PsuPin, Metric: SensorMetric.Current })
            .Select(group => new TabItem { Header = group.Key, Classes = { "vertical" }, Theme = itemTheme, Tag = group.ToArray() })
            .ToArray();
        tabs.SelectedIndex = 0;
        return tabs;
    }

    private static string MetricCategory(DebugMetricViewModel metric)
    {
        if (IsPinReading(metric))
        {
            var definition = metric.Definition;
            return definition.Metric switch
            {
                SensorMetric.Current when definition.Component == SensorComponent.PsuPin => $"Connector {definition.ConnectorId}",
                SensorMetric.Current => "Pin currents",
                SensorMetric.Voltage => "Pin voltages",
                SensorMetric.Power => "Pin power",
                _ => definition.Group
            };
        }

        return metric.Definition.Component == SensorComponent.PsuConnector ? "Connectors" : metric.Definition.Group;
    }

    private static Grid MetricColumns(params Control[] cells)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,90,90,135,190"),
            ColumnSpacing = 8,
            Margin = new Thickness(12, 6, 8 + PageScrollbarGutter, 6)
        };
        for (var index = 0; index < cells.Length; index++)
        {
            cells[index].VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(cells[index], index);
            row.Children.Add(cells[index]);
        }

        return row;
    }

    private static Control CreateMetric(DebugMetricViewModel metric)
    {
        var definition = metric.Definition;
        var label = definition.Component switch
        {
            SensorComponent.PsuPin => $"C{definition.ConnectorId} · {definition.Label}",
            SensorComponent.PsuConnector => $"Connector {definition.ConnectorId} current",
            SensorComponent.GpuPin when IsPinReading(metric) => $"P{definition.Channel} {definition.Label.ToLowerInvariant()}",
            _ => definition.Label
        };
        var mode = new ComboBox { ItemsSource = MetricModes, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(mode, $"{label} mode");
        BindTo(mode, SelectingItemsControl.SelectedIndexProperty, metric, nameof(metric.ModeIndex), BindingMode.TwoWay);

        var input = CreateOverrideInput(metric);
        AutomationProperties.SetName(input, $"{label} override");
        BindTo(input, IsEnabledProperty, metric, nameof(metric.IsValueEditable));

        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 6 };
        actions.Children.Add(input);
        if (metric.Definition.IsFault)
        {
            var flags = CreateFaultMenu(metric);
            Grid.SetColumn(flags, 1);
            actions.Children.Add(flags);
        }

        var reset = IconButton($"Reset {label}", "refresh", metric.Reset);
        Grid.SetColumn(reset, 2);
        actions.Children.Add(reset);

        var name = Header(label);
        name.TextWrapping = TextWrapping.Wrap;
        var validation = Caption();
        validation[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("ErrorBrush");
        BindTo(validation, TextBlock.TextProperty, metric, nameof(metric.Error));
        validation.Bind(IsVisibleProperty, new Binding(nameof(metric.Error))
        {
            Source = metric,
            Converter = StringConverters.IsNotNullOrEmpty
        });
        var nameCell = new StackPanel { Children = { name, validation } };
        var live = CreateReading(metric, nameof(metric.LiveText), () => metric.LiveText);
        var effective = CreateReading(metric, nameof(metric.EffectiveValueText), () => metric.EffectiveValueText);
        BindTo(effective, ToolTip.TipProperty, metric, nameof(metric.EffectiveQuality));
        return MetricColumns(nameCell, live, effective, mode, actions);
    }

    private static TextBlock CreateReading(DebugMetricViewModel metric, string property, Func<string> read)
    {
        var value = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        BindTo(value, TextBlock.TextProperty, metric, property);
        Watch(value, metric, () => value.Classes.Set("unreported-reading", read() == DebugMetricViewModel.NotReported), property);
        return value;
    }

    private static Control CreateOverrideInput(DebugMetricViewModel metric)
    {
        if (metric.Definition.IsBoolean)
        {
            return Switch("On", metric, nameof(metric.BooleanValue));
        }

        var number = new NumericUpDown
        {
            Classes = { "curve-point" },
            Minimum = (decimal)metric.Definition.Minimum,
            Maximum = (decimal)metric.Definition.Maximum,
            Increment = metric.Definition.IsInteger ? 1 : 0.1m,
            FormatString = metric.Definition.IsInteger ? "0" : "0.###",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        BindTo(number, NumericUpDown.ValueProperty, metric, nameof(metric.Value), BindingMode.TwoWay);
        return number;
    }

    private static Button CreateFaultMenu(DebugMetricViewModel metric)
    {
        var flags = new MenuFlyout();
        var items = new (int Bit, string Label)[]
        {
            (1, "Chip overtemperature"), (2, "Sensor overtemperature"), (4, "Total overcurrent"),
            (8, "Per-pin overcurrent"), (16, "Overpower"), (32, "Current imbalance")
        };
        foreach (var (bit, label) in items)
        {
            var item = new MenuItem { Header = label, ToggleType = MenuItemToggleType.CheckBox };
            flags.Opening += (_, _) => item.IsChecked = ((int)metric.Value & bit) != 0;
            item.Click += (_, _) => metric.Value = item.IsChecked ? (int)metric.Value | bit : (int)metric.Value & ~bit;
            flags.Items.Add(item);
        }

        var button = IconButton("Select fault bits", "info-action", () => { });
        button.Flyout = flags;
        BindTo(button, IsEnabledProperty, metric, nameof(metric.IsValueEditable));
        return button;
    }

    private static bool IsPinReading(DebugMetricViewModel metric) =>
        (metric.Definition.Component is SensorComponent.GpuPin or SensorComponent.PsuPin)
        && (metric.Definition.Metric is SensorMetric.Current or SensorMetric.Power or SensorMetric.Voltage);

    private static string PluginName(DebugPluginViewModel plugin) => plugin.Id == "generic-nvidia" ? "Nvidia" : plugin.Name;

}
