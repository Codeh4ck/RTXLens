using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using RTXLens.App.ViewModels.Shell;
using RTXLens.Contracts.Warnings;
using RTXLens.Presentation.Settings;
using RTXLens.WarningUI;
using static RTXLens.App.Views.Shell.DesktopControls;
using static RTXLens.App.Views.Diagnostics.WarningVisuals;

namespace RTXLens.App.Views.Diagnostics;

internal sealed class WarningsPage : UserControl
{
    public WarningsPage(ShellViewModel shell)
    {
        var tabs = new TabControl
        {
            Margin = new Thickness(0, 12, 0, 12),
            TabStripPlacement = Dock.Left,
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Spacing = 4 }),
            Theme = (ControlTheme)Application.Current!.FindResource("VerticalConfigurationTabStripTheme")!
        };
        tabs.ItemsSource = new[]
        {
            Tab("Active Warnings", ActiveWarnings(shell, () => tabs.SelectedIndex = 1)),
            Tab("History", new WarningHistoryView(shell))
        };
        tabs.SelectedIndex = 0;
        AutomationProperties.SetName(tabs, "Warnings");
        Content = tabs;
    }

    private static TabItem Tab(string header, Control content) => new()
    {
        Header = header, Content = content, Classes = { "vertical" },
        Theme = (ControlTheme)Application.Current!.FindResource("ConfigurationTabTheme")!
    };

    private static Control ActiveWarnings(ShellViewModel shell, Action openHistory)
    {
        var dashboard = shell.Dashboard;
        var state = new ContentControl();
        var count = new ContentControl { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        (string Summary, int Total, int Active, bool AllUnavailable)? previous = null;
        Watch(state, dashboard, () =>
        {
            var current = dashboard.CurrentWarnings;
            var active = current.Count(w => w.Phase == WarningPhase.Active);
            var allUnavailable = current.All(w => w.Phase == WarningPhase.Unavailable);
            var display = (dashboard.WarningSummary, current.Count, active, allUnavailable);
            if (previous == display)
            {
                return;
            }

            previous = display;
            count.Content = current.Count == 0 ? null
                : Badge(active > 0 ? $"{active} active" : $"{current.Count} pending", active > 0 ? "error" : "warning");

            state.Content = dashboard.HasWarningSnapshot && current.Count == 0
                ? StatePanel("No active warnings", "Past warnings and their captured readings are available in History.", "success",
                    ActionButton("Open history", DesktopIcons.Info, openHistory))
                : current.Count == 0 || allUnavailable
                    ? StatePanel("Warning status unavailable", dashboard.WarningSummary, "warning")
                    : null;
            state.IsVisible = state.Content is not null;
        }, nameof(dashboard.WarningSummary));
        var cards = new ItemsControl
        {
            ItemsSource = dashboard.CurrentWarnings,
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Spacing = 10 }),
            ItemTemplate = new FuncDataTemplate<WarningState>((warning, _) => warning is null ? null : Card(warning, shell.IsSimulation))
        };
        var heading = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8,
            Children = { new TextBlock { Text = "Active Warnings", Classes = { "heading" }, TextWrapping = TextWrapping.Wrap }, count }
        };
        Grid.SetColumn(count, 1);
        return Scroll(new StackPanel { Spacing = 12, Children = { heading, state, cards } });
    }

    private static Control Card(WarningState state, bool simulation)
    {
        var warning = PopupWarning.FromState(state, simulation);
        var active = state.Phase == WarningPhase.Active;
        var tone = active ? "error" : "warning";
        var status = Badge(state.Phase == WarningPhase.Unavailable ? "Unable to verify" : state.Phase.ToString(), tone);
        var title = Field(warning.Source, warning.Title, 19);
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12,
            Children = { Glyph(tone, 40), title, status }
        };
        Grid.SetColumn(title, 1);
        Grid.SetColumn(status, 2);

        var body = new StackPanel { Spacing = 12, Children = { header } };
        if (warning.HasReadings)
        {
            body.Children.Add(Readings(warning.Readings, active));
        }

        if (warning.Detail.Length > 0)
        {
            body.Children.Add(DialogText(warning.Detail));
        }

        if (warning.Context.Length > 0)
        {
            body.Children.Add(Caption(warning.Context));
        }

        var metadata = new WrapPanel { ItemSpacing = 20, LineSpacing = 8,
            Children = { Field("Started", state.StartedAt.LocalDateTime.ToString("G")), Field("Device", state.DeviceId) } };
        if (state.LastNotificationAt is { } notified)
        {
            metadata.Children.Add(Field("Last notification", notified.LocalDateTime.ToString("G")));
        }

        body.Children.Add(metadata);
        return new Border
        {
            Padding = new Thickness(16), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(3, 1, 1, 1),
            [!Border.BackgroundProperty] = new DynamicResourceExtension(active ? "ErrorSurfaceBrush" : "WarningSurfaceBrush"),
            [!Border.BorderBrushProperty] = new DynamicResourceExtension(active ? "ErrorBrush" : "WarningBrush"), Child = body
        };
    }
}
