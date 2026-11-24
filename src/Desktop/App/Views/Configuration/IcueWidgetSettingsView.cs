using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using RTXLens.App.ViewModels.Shell;
using RTXLens.Contracts.Configuration;
using RTXLens.Presentation.Configuration;
using RTXLens.Presentation.Settings;
using RTXLens.Presentation.Telemetry;
using static RTXLens.App.Views.Shell.DesktopControls;

namespace RTXLens.App.Views.Configuration;

internal sealed class IcueWidgetSettingsView : UserControl
{
    private readonly ShellViewModel _shell;
    private readonly ConfigurationEditorViewModel _editor;

    private readonly TabControl _tabs = new()
    {
        TabStripPlacement = Dock.Left,
        ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Spacing = 4 }),
        Theme = (ControlTheme)Application.Current!.FindResource("VerticalConfigurationTabStripTheme")!
    };

    private string _profile = "Default";

    private static readonly string[] SourceArray = ["Panels and layout", "Readings and alerts", "Widget Personalization"];

    public IcueWidgetSettingsView(ShellViewModel shell)
    {
        _shell = shell;
        _editor = shell.Configuration;

        var tabs = SourceArray.Select(name => new TabItem
        {
            Header = name, Classes = { "vertical" },
            Theme = (ControlTheme)Application.Current!.FindResource("ConfigurationTabTheme")!
        }).ToArray();

        _tabs.ItemsSource = tabs;
        _tabs.SelectedIndex = 0;

        _tabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.Source, _tabs))
            {
                Refresh();
            }
        };

        AutomationProperties.SetName(_tabs, "Widget settings");
        Content = _tabs;

        BindTo(this, IsEnabledProperty, _editor, nameof(_editor.CanEdit));
        Watch(this, _editor, Refresh, nameof(_editor.Widget));

        Refresh();
    }

    private void Refresh()
    {
        if (_tabs.SelectedItem is not TabItem tab)
        {
            return;
        }

        if (!_editor.Widget.ProfileNames.Contains(_profile))
        {
            _profile = "Default";
        }

        var body = new StackPanel { Spacing = 12 };
        var group = (string)tab.Header!;
        var settings = WidgetSettingDefinition.Managed.Where(setting => setting.Group == group);

        switch (group)
        {
            case "Panels and layout":
                body.Children.Add(ProfileManagement());
                break;
            case "Readings and alerts":
                {
                    var copy = ActionButton("Copy desktop preferences", "chart-import", CopyDesktopPreferences);
                    copy.HorizontalAlignment = HorizontalAlignment.Right;

                    body.Children.Add(copy);
                    break;
                }
        }

        var inputs = new Dictionary<string, Control>();
        var panelCards = new List<Control>();

        foreach (var section in settings.GroupBy(setting => setting.Section).OrderBy(section => section.Key == "GPU status" ? 0 : 1))
        {
            var panelEditor = group == "Panels and layout" && section.Key.StartsWith("Panel ", StringComparison.Ordinal);
            var rows = new StackPanel { Spacing = panelEditor ? 12 : 6 };

            foreach (var setting in section)
            {
                var title = panelEditor && setting.Id == $"panel{section.Key[^1]}" ? "Content" : setting.Label;
                if (setting.Unit is { Length: > 0 } unit && !title.Contains($"({unit}", StringComparison.Ordinal))
                {
                    title += $" ({setting.Unit})";
                }

                if (setting.Id == "warningPreview")
                {
                    title = "Warning preview";
                }

                var input = setting.Id == "warningPreview" ? PreviewControl(setting) : Input(setting, title);
                inputs.Add(setting.Id, input);

                if (panelEditor)
                {
                    rows.Children.Add(ConfigurationPage.Field(title, input));
                }
                else
                {
                    var editorColumn = new Grid
                    {
                        ColumnDefinitions =
                        {
                            new ColumnDefinition(1, GridUnitType.Star)
                            {
                                MaxWidth = setting.Id == "precision" ? double.PositiveInfinity : 300
                            }
                        },
                        Children = { input }
                    };

                    rows.Children.Add(Setting(title, editorColumn));
                }
            }

            var card = Section(section.Key, rows);

            card.HeaderActions = section.Key switch
            {
                "GPU status" => Help("Optional GPU clocks", "Crossbar and System appear when the selected NVIDIA GPU reports them.", "Choose Always show Crossbar and System clocks to keep both readings beneath the core and memory clocks, alongside VRAM, temperatures and fans. Compact slots otherwise offer a clock page switch. Hiding fans also keeps both clocks visible."),
                "Pin charts" => Help("Pin chart scales", "The widget follows the GPU chart profile associated with the displayed device. Enable an override to use the widget’s current maximum or fallback power.",
                    "Without a matching chart profile, the widget uses its own scale. Fallback power applies only when both the fixed per-pin power maximum and the cable rating are unavailable.",
                    "Display ranges do not change protection limits."),
                "Panel appearance" => Help("Widget appearance", "Choose slot size, base colors and background media in iCUE. The controls here style the RTX Lens panels inside that slot."),
                _ => card.HeaderActions
            };

            if (panelEditor)
            {
                panelCards.Add(card);
            }
            else
            {
                body.Children.Add(card);
            }
        }

        if (panelCards.Count > 0)
        {
            body.Children.Add(AdaptiveCards(width => width >= 990 ? 3 : width >= 660 ? 2 : 1, [.. panelCards]));
        }

        if (_profile != "Default")
        {
            body.Children.Add(ActionButton("Use Default for this category", "refresh", () =>
            {
                foreach (var setting in WidgetSettingDefinition.Managed.Where(setting => setting.Group == group))
                {
                    _editor.SetWidgetValue(_profile, setting.Id, null);
                }

                Refresh();
            }));
        }

        LinkToggle("hideGpuFans", "showFanDuty", false);

        if (inputs.TryGetValue("alwaysShowInternalClocks", out var clockMode))
        {
            var always = (ToggleSwitch)clockMode;
            var hideFans = (ToggleSwitch)inputs["hideGpuFans"];

            void UpdateClockOptions()
            {
                always.IsEnabled = hideFans.IsChecked != true;
                var individual = hideFans.IsChecked != true && always.IsChecked != true;
                inputs["showCrossbarClock"].IsEnabled = individual;
                inputs["showSystemClock"].IsEnabled = individual;
            }

            always.IsCheckedChanged += (_, _) => UpdateClockOptions();
            hideFans.IsCheckedChanged += (_, _) => UpdateClockOptions();

            UpdateClockOptions();
        }

        LinkToggle("overrideCurrentMax", "currentMax", true);
        LinkToggle("overridePowerFallback", "powerCableFallback", true);
        LinkToggle("reduceMotion", "alertPulse", false);

        for (var slot = 1; slot <= 3; slot++)
        {
            var key = $"panel{slot}";
            if (!inputs.TryGetValue(key, out var content))
            {
                continue;
            }

            var source = (ComboBox)content;
            var summary = inputs[key + "Hero"];

            source.SelectionChanged += (_, _) => UpdateSummary();

            UpdateSummary();
            continue;

            void UpdateSummary() => summary.IsEnabled = source.SelectedItem is WidgetSettingOption { Key: not ("asusPins" or "wireviewPins" or "off") };
        }

        tab.Content = body;
        return;

        void LinkToggle(string sourceId, string targetId, bool enabledWhenChecked)
        {
            if (!inputs.TryGetValue(sourceId, out var input))
            {
                return;
            }

            var source = (ToggleSwitch)input;
            var target = inputs[targetId];

            source.IsCheckedChanged += (_, _) => Update();
            Update();

            return;

            void Update() => target.IsEnabled = source.IsChecked == enabledWhenChecked;
        }
    }

    private Control ProfileManagement()
    {
        var profiles = Select("Widget profile", _editor.Widget.ProfileNames, _profile, name =>
        {
            _profile = name;
            Refresh();
        });

        profiles.Width = 220;

        var instructions = Caption("Enter this profile name in iCUE to use its settings.");

        var add = IconButton("Add widget profile", "add-action", async () =>
        {
            if (TopLevel.GetTopLevel(this) is not Window owner ||
                await RequestProfileNameAsync(owner, "New widget profile") is not { } name)
            {
                return;
            }

            if (!_editor.AddWidgetProfile(name))
            {
                instructions.Text = "Choose a unique name of up to 80 characters. Default and Instance are reserved.";
                return;
            }
            _profile = name;
            Refresh();
        });

        var delete = IconButton("Delete widget profile", DesktopIcons.ChartClear, async () =>
        {
            var name = _profile;
            if (TopLevel.GetTopLevel(this) is Window owner && await ConfirmAsync(owner, "Delete widget profile", $"Delete {name}? Widgets using it will follow Default.", "Delete"))
            {
                _editor.DeleteWidgetProfile(name);
                _profile = "Default";

                Refresh();
            }
        });

        delete.IsEnabled = _profile != "Default";

        var info = Help("Widget profiles", "Custom profiles inherit from Default until you change a setting. Apply saves your edits.",
            "In iCUE, enter the profile name for each widget. Enter Instance to use its existing iCUE settings.",
            "Slot size, Custom Style and background media are configured in iCUE.");

        var panel = Section("Widget profile", instructions);
        panel.HeaderActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { profiles, add, delete, info } };

        return panel;
    }

    private Control Input(WidgetSettingDefinition setting, string title)
    {
        var value = _editor.Widget.Value(_profile, setting);
        Control control;

        switch (setting.Type)
        {
            case "switch":
                var toggle = new ToggleSwitch { IsChecked = value == "true" };
                toggle.IsCheckedChanged += (_, _) => Save(toggle.IsChecked == true ? "true" : "false");

                control = toggle;
                break;
            case "combobox" when setting.Id == "precision":
                var choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                foreach (var choice in setting.Options!)
                {
                    var radio = new RadioButton { Content = choice.Label, IsChecked = value == choice.Key };
                    radio.IsCheckedChanged += (_, _) => { if (radio.IsChecked == true)
                        {
                            Save(choice.Key);
                        }
                    };
                    choices.Children.Add(radio);
                }
                control = new ScrollViewer
                {
                    Content = choices, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
                };
                break;
            case "combobox":
                var options = setting.Options!;
                var combo = new ComboBox { ItemsSource = options, SelectedItem = options.First(x => x.Key == value) };

                combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is WidgetSettingOption choice)
                    {
                        Save(choice.Key);
                    }
                };

                control = combo;
                break;
            case "slider":
                var slider = new Slider
                {
                    Minimum = setting.Min!.Value, Maximum = setting.Max!.Value,
                    Value = int.Parse(value, CultureInfo.InvariantCulture),
                    IsSnapToTickEnabled = true, TickFrequency = 1, SmallChange = 1,
                    LargeChange = Math.Min(10, setting.Max.Value - setting.Min.Value)
                };
                AutomationProperties.SetName(slider, title);
                var readout = new TextBlock
                {
                    Text = value, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center
                };
                slider.ValueChanged += (_, args) =>
                {
                    var formatted = args.NewValue.ToString("0", CultureInfo.InvariantCulture);
                    readout.Text = formatted;
                    Save(formatted);
                };
                Grid.SetColumn(readout, 1);
                control = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,40"), ColumnSpacing = 10,
                    Children = { slider, readout }
                };
                break;
            case "color":
                var picker = new ColorPicker { IsAlphaEnabled = false, Color = Color.Parse(value) };

                picker.PropertyChanged += (_, e) =>
                {
                    if (e.Property == ColorView.ColorProperty)
                    {
                        Save($"#{picker.Color.R:x2}{picker.Color.G:x2}{picker.Color.B:x2}");
                    }
                };

                control = picker;
                break;
            default:
                var text = new TextBox { Text = value, MaxLength = 2000, MinWidth = 170 };

                text.PropertyChanged += (_, e) =>
                {
                    if (e.Property == TextBox.TextProperty)
                    {
                        Save(text.Text ?? "");
                    }
                };

                control = text;
                break;
        }

        AutomationProperties.SetName(control, title);
        control.HorizontalAlignment = control is ToggleSwitch or ColorPicker ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;

        return control;

        void Save(string text)
        {
            var inherited = _profile == "Default" ? setting.Initial : _editor.Widget.Value("Default", setting);
            _editor.SetWidgetValue(_profile, setting.Id, text == inherited ? null : text);
        }
    }

    private Control PreviewControl(WidgetSettingDefinition setting)
    {
        var choices = setting.Options!.Where(option => option.Key != "none").ToArray();
        var scenarios = new ComboBox { ItemsSource = choices, SelectedIndex = 0 };

        AutomationProperties.SetName(scenarios, "Simulated warning");

        var status = Caption("Sends a one-time example to connected widgets using this profile.");
        var preview = ActionButton("Queue preview", "display-action", async () =>
        {
            if (scenarios.SelectedItem is not WidgetSettingOption scenario)
            {
                return;
            }

            status.Text = await _editor.QueueWidgetPreviewAsync(_profile, scenario.Key, CancellationToken.None)
                ? "Preview queued for " + _profile + "."
                : "Preview could not be queued. " + _editor.Message;
        });

        scenarios.HorizontalAlignment = HorizontalAlignment.Stretch;
        preview.HorizontalAlignment = HorizontalAlignment.Stretch;
        status.TextWrapping = TextWrapping.Wrap;

        return new StackPanel { Spacing = 6, Children = { scenarios, preview, status } };
    }

    private void CopyDesktopPreferences()
    {
        var preferences = _shell.Preferences;
        var pinMetric = preferences.PinMetric switch { PinMetric.Power => "W", PinMetric.Voltage => "V", _ => "A" };

        var values = new Dictionary<string, string>
        {
            ["temperatureUnit"] = preferences.Fahrenheit ? "°F" : "°C",
            ["asusMetric"] = pinMetric, ["wireviewMetric"] = pinMetric,
            ["showPeak"] = preferences.ShowPeakHold ? "true" : "false",
            ["showHeadroom"] = preferences.ShowHeadroom ? "true" : "false",
            ["asusCompare"] = preferences.ShowAsusPsuCurrent ? "true" : "false",
            ["wireviewCompare"] = preferences.ShowWireViewPsuCurrent ? "true" : "false",
            ["asusConnector"] = preferences.AsusPsuConnector.ToString(CultureInfo.InvariantCulture),
            ["wireviewConnector"] = preferences.WireViewPsuConnector.ToString(CultureInfo.InvariantCulture),
            ["checkUpdates"] = preferences.AutomaticallyCheckForUpdates ? "true" : "false",
            ["updateFrequency"] = preferences.UpdateFrequency.ToString()
        };

        foreach (var (key, value) in values)
        {
            _editor.SetWidgetValue(_profile, key, value);
        }

        Refresh();
    }

    private static ComboBox Select(string label, IReadOnlyList<string> options, string current, Action<string> change)
    {
        var combo = new ComboBox { ItemsSource = options, SelectedItem = current };

        AutomationProperties.SetName(combo, label);

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is string selected)
            {
                change(selected);
            }
        };

        return combo;
    }

    private Button Help(string title, string message, params string[] details) => IconButton(title, DesktopIcons.Info, async () =>
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            await ShowInformationAsync(owner, title, UsageGuide(message, details));
        }
    });
}
