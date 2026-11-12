using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Svg;
using RTXLens.App.Controls;

namespace RTXLens.App.Views.Shell;

internal static class DesktopControls
{
    public const double PageScrollbarGutter = 14;
    public const double NavigationInset = 7;

    private static readonly Dictionary<string, IImage> Icons = new(StringComparer.Ordinal);
    private static readonly Geometry CheckIcon = Geometry.Parse("M 4,12 L 6,10 L 9,13 L 18,4 L 20,6 L 9,17 Z");

    public static void BindTo(StyledElement target, AvaloniaProperty property, object source, string path,
        BindingMode mode = BindingMode.OneWay) => target.Bind(property, new Binding(path) { Source = source, Mode = mode });

    public static void Watch(Control control, System.ComponentModel.INotifyPropertyChanged source, Action apply, string? property = null)
    {
        control.AttachedToVisualTree += (_, _) =>
        {
            apply();
            source.PropertyChanged += OnPropertyChanged;
        };
        control.DetachedFromVisualTree += (_, _) => source.PropertyChanged -= OnPropertyChanged;

        void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (property is null || args.PropertyName is null or "" || args.PropertyName == property)
            {
                apply();
            }
        }
    }

    public static IImage Icon(string name)
    {
        if (!Icons.TryGetValue(name, out var image))
        {
            var assembly = typeof(DesktopControls).Assembly.GetName().Name;
            var path = $"avares://{assembly}/Assets/Icons/{name}.svg";
            image = new SvgImage { Source = SvgSource.Load(path, null) };
            Icons.Add(name, image);
        }

        return image;
    }

    public static Button ActionButton(string text, string icon, Action action) =>
        ActionButton(text, new Image { Source = Icon(icon), Width = 15, Height = 15 }, action);

    private static Button ActionButton(string text, Control icon, Action action)
    {
        var button = new Button
        {
            Content = new StackPanel
            {
                Classes = { "button-content" },
                Children = { icon, new TextBlock { Text = text } }
            }
        };

        Avalonia.Automation.AutomationProperties.SetName(button, text);
        button.Click += (_, _) => action();
        return button;
    }

    public static Button CommitButton(string text, Action action)
    {
        var icon = new PathIcon { Data = CheckIcon, Width = 15, Height = 15 };
        icon[!TemplatedControl.ForegroundProperty] = new DynamicResourceExtension("IconBrush");

        var button = ActionButton(text, icon, action);
        button.Classes.Add("primary-action");
        return button;
    }

    public static Button IconButton(string name, string icon, Action action)
    {
        var button = new Button
        {
            Classes = { "desktop-action" },
            Width = 36,
            Height = 36,
            Padding = new Thickness(0),
            Content = new Image { Source = Icon(icon), Width = 16, Height = 16 },
            VerticalAlignment = VerticalAlignment.Center
        };
        Avalonia.Automation.AutomationProperties.SetName(button, name);
        ToolTip.SetTip(button, name);
        button.Click += (_, _) => action();
        return button;
    }

    public static TextBlock Caption(string? text = null) =>
        new() { Text = text, Classes = { "caption" }, TextWrapping = TextWrapping.Wrap };

    public static SectionPanel Section(string title, Control content) =>
        new() { Header = title, Content = content, VerticalAlignment = VerticalAlignment.Top };

    public static ScrollViewer Scroll(Control content) => new()
    {
        Content = content,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };

    public static Grid Columns(params Control[] controls)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        for (var index = 0; index < controls.Length; index++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            Grid.SetColumn(controls[index], index);
            grid.Children.Add(controls[index]);
        }

        return grid;
    }

    public static void StackAtNarrowWidth(Grid grid, double breakpoint, double firstColumnWeight = 1)
    {
        if (grid.Children.Count < 2)
        {
            return;
        }

        var spacing = grid.ColumnSpacing;
        grid.SizeChanged += (_, args) =>
        {
            var compact = args.NewSize.Width < breakpoint;
            grid.RowSpacing = compact ? spacing : 0;
            grid.ColumnSpacing = compact ? 0 : spacing;
            for (var index = 0; index < grid.Children.Count; index++)
            {
                var weight = index == 0 && !compact ? firstColumnWeight : 1;
                grid.ColumnDefinitions[index].Width = compact && index != 0
                    ? new GridLength(0) : new GridLength(weight, GridUnitType.Star);
                Grid.SetColumn(grid.Children[index], compact ? 0 : index);
                Grid.SetRow(grid.Children[index], compact ? index : 0);
            }
        };

        for (var index = 0; index < grid.Children.Count; index++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }
    }

    public static Control IconLabel(string text, string? icon, double size, bool caption = false)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = caption ? 12 : 15,
            FontWeight = caption ? FontWeight.Normal : FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        if (caption)
        {
            label.Classes.Add("caption");
        }

        if (icon is null)
        {
            return label;
        }

        var glyph = new Border
        {
            Classes = { "icon-surface" },
            Width = size,
            Height = size,
            Padding = new Thickness(size * 0.18),
            Child = new Image { Source = Icon(icon) }
        };
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { glyph, label }
        };
        Grid.SetColumn(label, 1);
        return row;
    }

    public static SectionPanel DashboardSection(string title, string? icon, Control content)
    {
        var panel = Section(title, content);
        panel.Classes.Add("dashboard");
        panel.HeaderIcon = icon is null ? null : Icon(icon);
        return panel;
    }

    public static Grid AdaptiveCards(Func<double, int> columnCount, params Control[] cards)
    {
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        foreach (var card in cards)
        {
            grid.Children.Add(card);
        }

        var columns = 0;

        ArrangeCards(double.PositiveInfinity);
        grid.SizeChanged += (_, args) => ArrangeCards(args.NewSize.Width);
        return grid;

        void ArrangeCards(double width)
        {
            var count = columnCount(width);
            if (count == columns)
            {
                return;
            }

            columns = count;
            grid.ColumnDefinitions.Clear();
            grid.RowDefinitions.Clear();
            for (var index = 0; index < columns; index++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            }

            for (var index = 0; index < (cards.Length + columns - 1) / columns; index++)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }

            for (var index = 0; index < cards.Length; index++)
            {
                Grid.SetColumn(cards[index], index % columns);
                Grid.SetRow(cards[index], index / columns);
            }
        }
    }

    public static ContentControl Message(string? text = null, string tone = "warning", bool badge = false)
    {
        var message = new ContentControl { Content = text, Classes = { badge ? "badge" : "banner", tone } };
        if (Application.Current?.TryFindResource("StatusMessageTheme", out var theme) == true)
        {
            message.Theme = (ControlTheme?)theme;
        }

        return message;
    }

    public static ContentControl Alert(string? text = null, string tone = "info", Action? dismiss = null)
    {
        var alert = Message(text, tone);
        alert.IsVisible = !string.IsNullOrWhiteSpace(text);
        alert.ContentTemplate = new FuncDataTemplate<string>((message, _) =>
        {
            var label = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            if (dismiss is null)
            {
                return label;
            }

            var close = IconButton("Dismiss alert", "cancel-action", dismiss);
            close.Classes.Add("alert-dismiss");
            close.Width = close.Height = 26;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Children = { label, close } };
            Grid.SetColumn(close, 1);
            return row;
        });
        return alert;
    }

    public static void SetAlert(ContentControl alert, string? text, string tone = "info")
    {
        alert.Classes.Set("info", tone == "info");
        alert.Classes.Set("success", tone == "success");
        alert.Classes.Set("warning", tone == "warning");
        alert.Classes.Set("error", tone == "error");
        alert.Content = text;
        alert.IsVisible = !string.IsNullOrWhiteSpace(text);
    }

    public static HeaderedContentControl Setting(string title, Control input)
    {
        var row = new HeaderedContentControl { Header = title, Content = input };
        if (Application.Current?.TryFindResource("SettingsRowTheme", out var theme) == true)
        {
            row.Theme = (ControlTheme?)theme;
        }

        return row;
    }

    public static Control UsageGuide(string introduction, params string[] steps)
    {
        var guide = new StackPanel { Spacing = 12 };
        guide.Children.Add(DialogText(introduction));

        for (var index = 0; index < steps.Length; index++)
        {
            var number = new TextBlock { Text = $"{index + 1}.", Classes = { "guide-step" } };
            var text = DialogText(steps[index]);
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                ColumnSpacing = 8,
                Children = { number, text }
            };
            Grid.SetColumn(text, 1);
            guide.Children.Add(row);
        }

        return guide;
    }

    public static TextBlock DialogText(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    public static Task<bool> ShowInformationAsync(Window owner, string title, Control content) =>
        ShowPromptAsync(owner, title, content, "Close", canCancel: false);

    public static Task<bool> ConfirmAsync(Window owner, string title, string message, string action) =>
        ConfirmAsync(owner, title, DialogText(message), action);

    public static Task<bool> ConfirmAsync(Window owner, string title, Control content, string action) =>
        ShowPromptAsync(owner, title, content, action, canCancel: true);

    public static Task<string?> RequestProfileNameAsync(Window owner, string title)
    {
        var dialog = CreatePromptWindow(title);
        var input = new TextBox { MaxLength = 80, Classes = { "profile-name" } };
        Avalonia.Automation.AutomationProperties.SetName(input, "Profile name");

        var create = CommitButton("Create profile", () => dialog.Close(input.Text?.Trim()));
        create.IsDefault = true;
        create.IsEnabled = false;
        input.TextChanged += (_, _) => create.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);

        var cancel = ActionButton("Cancel", "cancel-action", () => dialog.Close());
        cancel.IsCancel = true;

        dialog.Body = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new StackPanel { Spacing = 6, Children = { new TextBlock { Text = "Profile name" }, input } },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, create }
                }
            }
        };

        dialog.Opened += (_, _) => input.Focus();
        return dialog.ShowDialog<string?>(owner);
    }

    private static LensWindow CreatePromptWindow(string title) => new()
    {
        Title = title,
        Width = 450,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterScreen
    };

    private static Task<bool> ShowPromptAsync(Window owner, string title, Control content, string action, bool canCancel)
    {
        var dialog = CreatePromptWindow(title);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        if (canCancel)
        {
            buttons.Children.Add(ActionButton("Cancel", "cancel-action", () => dialog.Close(false)));
        }

        buttons.Children.Add(CommitButton(action, () => dialog.Close(true)));
        dialog.Body = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children = { content, buttons }
        };

        return dialog.ShowDialog<bool>(owner);
    }
}
