using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Styling;
using RTXLens.App.ViewModels.Shell;
using RTXLens.Contracts.Warnings;
using RTXLens.Presentation.Configuration;
using static RTXLens.App.Views.Configuration.ConfigurationPage;
using static RTXLens.App.Views.Shell.DesktopControls;

namespace RTXLens.App.Views.Configuration;

internal sealed class WarningsConfigurationView : UserControl
{
    public WarningsConfigurationView(ShellViewModel shell)
    {
        var sections = new TabControl
        {
            TabStripPlacement = Dock.Left,
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Spacing = 4 }),
            Theme = (ControlTheme)Application.Current!.FindResource("VerticalConfigurationTabStripTheme")!
        };
        AutomationProperties.SetName(sections, "Warning settings");
        sections.SelectionChanged += (_, args) =>
        {
            if (ReferenceEquals(args.Source, sections) && sections.SelectedItem is TabItem { Tag: Func<Control> create } selected)
            {
                selected.Tag = null;
                selected.Content = create();
            }
        };
        sections.ItemsSource = new[]
        {
            WarningTab("Device warning profiles", () => new DeviceWarningProfilesView(shell)),
            WarningTab("GPU temperatures", () => CreateTemperatureWarnings(shell.Configuration)),
            WarningTab("Notifications", () => CreateNotifications(shell.Configuration)),
            WarningTab("Per-warning overrides", () => CreateOverrides(shell.Configuration)),
            WarningTab("Current warnings", () => CreateCurrentWarnings(shell))
        };
        sections.SelectedIndex = 0;

        Content = sections;
    }

    private static TabItem WarningTab(string label, Func<Control> create) => new()
    {
        Header = label,
        Classes = { "vertical" },
        Theme = (ControlTheme)Application.Current!.FindResource("ConfigurationTabTheme")!,
        Tag = create
    };

    private static Control CreateTemperatureWarnings(ConfigurationEditorViewModel editor)
    {
        var temperature = Section("NVIDIA GPU temperature warnings", new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        });
        var temperatureFields = (ContentControl)temperature.Content!;
        Watch(temperature, editor, () =>
        {
            var model = editor.NvidiaWarnings;
            temperatureFields.Content = model is null ? Alert("The NVIDIA plugin is unavailable.", "warning") : Columns(
                Temperature("Core", model, nameof(model.CoreWarningEnabled), nameof(model.CoreTemperatureWarningC)),
                Temperature("VRAM", model, nameof(model.MemoryWarningEnabled), nameof(model.MemoryTemperatureWarningC)));
            if (model is not null)
            {
                BindTo((Control)temperatureFields.Content!, IsEnabledProperty, editor, nameof(editor.CanEdit));
            }
        }, nameof(editor.NvidiaWarnings));
        return temperature;
    }

    private static Control CreateNotifications(ConfigurationEditorViewModel editor)
    {
        var policies = editor.Warnings;
        var notificationFields = Stack(
            Alert("Pick when RTX Lens should give you a heads-up. Hit Apply to use these settings in the app and widget."),
            Switch("Enable warning notifications", policies, nameof(policies.NotificationsEnabled)),
            PolicyFields(policies.Defaults));
        BindTo(notificationFields, IsEnabledProperty, editor, nameof(editor.CanEdit));
        var notifications = Section("Warning notifications", notificationFields);
        notifications.HeaderActions = IconButton("How warning notifications work", "info-action", async void () =>
        {
            if (TopLevel.GetTopLevel(notifications) is Window owner)
            {
                await ShowInformationAsync(owner, "Warning notifications", Stack(
                    Caption("Notify when a warning starts sends one notification. Repeat while active sends reminders at the selected interval."),
                    Caption("For example, crossing 80 °C can send an initial warning. A subsequent rise to 82 °C updates the details. "
                        + "The next reminder follows the configured interval."),
                    Caption("Reminders pause while recovery is being confirmed or readings are unavailable. "
                        + "After confirmed recovery, another occurrence can send a new notification."),
                    Caption("Use global defaults applies the shared policy. An override can change the initial notification, reminder interval, "
                        + "and clear duration. Windows notifications also require the local switch under Settings.")));
            }
        });

        return notifications;
    }

    private static Control CreateOverrides(ConfigurationEditorViewModel editor)
    {
        var policies = editor.Warnings;
        var picker = new ComboBox
        {
            ItemsSource = policies.Overrides,
            ItemTemplate = new FuncDataTemplate<WarningOverrideEditorViewModel>((item, _) =>
                new TextBlock { Text = item?.Name })
        };
        var overrideFields = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is WarningOverrideEditorViewModel selected)
            {
                var custom = PolicyFields(selected.Policy);
                BindTo(custom, IsEnabledProperty, selected, nameof(selected.IsCustom));
                overrideFields.Content = Stack(Switch("Use global defaults", selected, nameof(selected.UseDefaults)), custom);
            }
            else
            {
                overrideFields.Content = null;
            }
        };
        Watch(picker, policies, () =>
        {
            if (picker.SelectedIndex < 0 && policies.HasDefinitions)
            {
                picker.SelectedIndex = 0;
            }
        }, nameof(policies.HasDefinitions));
        var overrideBody = Stack(
            Alert("Pick a warning from the Warning type dropdown to change how often it shows up. It lists warnings from every installed plugin."),
            Field("Warning type", picker), overrideFields);
        BindTo(overrideBody, IsEnabledProperty, editor, nameof(editor.CanEdit));

        return Section("Per-warning overrides", overrideBody);
    }

    private static Control CreateCurrentWarnings(ShellViewModel shell)
    {
        var summary = Alert();
        var dashboard = shell.Dashboard;
        Watch(summary, dashboard, () =>
        {
            var message = dashboard.WarningSummary;
            SetAlert(summary, message, message.StartsWith("Service unavailable", StringComparison.Ordinal) ? "warning" : "info");
        }, nameof(dashboard.WarningSummary));
        var current = new ItemsControl
        {
            ItemsSource = shell.Dashboard.CurrentWarnings,
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Spacing = 6 }),
            ItemTemplate = new FuncDataTemplate<WarningState>((warning, _) => warning is null ? null : new Border
            {
                Classes = { "panel-frame" },
                Padding = new Thickness(12),
                Child = Stack(Setting(warning.Title, Message(warning.Phase.ToString(), badge: true)), Caption(warning.Detail))
            })
        };

        return Section("Current warnings", Stack(summary, current,
            Alert("Recovering pauses reminders until the clear duration is confirmed. Unavailable readings cannot establish recovery.")));
    }

    private static Control Temperature(string label, GenericNvidiaSettingsEditorViewModel model, string enable, string threshold)
    {
        var value = NumberField($"{label} warning threshold (°C)", model, threshold, 30, 120);
        BindTo(value, IsEnabledProperty, model, enable);
        return Stack(Switch($"{label} temperature warning", model, enable), value);
    }

    internal static StackPanel PolicyFields(WarningPolicyEditorViewModel model)
    {
        var reminder = NumberField("Repeat every (seconds)", model, nameof(model.RepeatIntervalSeconds), 5, 3600);
        BindTo(reminder, IsEnabledProperty, model, nameof(model.RepeatWhileActive));
        return Stack(Switch("Notify when a warning starts", model, nameof(model.NotifyOnStart)),
            Switch("Repeat while warning remains active", model, nameof(model.RepeatWhileActive)),
            Columns(reminder, NumberField("Condition must remain clear for (seconds)", model, nameof(model.ClearAfterSeconds), 0, 60)));
    }

    internal static StackPanel Stack(params Control[] controls)
    {
        var stack = new StackPanel { Spacing = 12 };
        stack.Children.AddRange(controls);
        return stack;
    }

    internal static NumericUpDown Number(decimal min, decimal max, decimal step) => new()
    {
        Minimum = min,
        Maximum = max,
        Increment = step,
        FormatString = step == 1 ? "0" : "0.##"
    };

    internal static int Whole(NumericUpDown input, string label) => input.Value is { } value && value == decimal.Truncate(value)
        ? decimal.ToInt32(value) : throw new ArgumentException($"{label} requires a whole number.");

    internal static double ReadNumber(NumericUpDown input, string label) => input.Value is { } value
        ? (double)value : throw new ArgumentException($"Enter {label}.");
}
