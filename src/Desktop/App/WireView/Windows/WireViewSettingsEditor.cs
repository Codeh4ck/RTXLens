using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using RTXLens.App.Controls;
using Windows.UI;
using RTXLens.Plugins.WireView.Contracts.Common;

namespace RTXLens.App.WireView.Windows;

internal sealed class WireViewSettingsEditor
{
    private static readonly string[] FaultNames = ["Chip overtemperature", "Sensor overtemperature", "Total overcurrent",
        "Per-pin overcurrent", "Overpower", "Current imbalance"];
    private static readonly string[] ScreenNames = ["Main", "Simple", "Current", "Temperature", "Status"];

    private readonly DeviceConfig _original;
    private readonly List<Func<DeviceConfig, DeviceConfig>> _readFields = [];
    private Grid _section = new();

    public StackPanel Content { get; } = new() { Spacing = 14 };

    public WireViewSettingsEditor(DeviceConfig config)
    {
        _original = config;
        AddSection("General");
        var name = new TextBox { Text = config.FriendlyName, MaxLength = WireViewSettingsValidation.MaximumDeviceNameLength };
        name.BeforeTextChanging += (_, args) => args.Cancel = args.NewText.Any(c => c is < ' ' or > '~');
        AddField("Device name", name);
        _readFields.Add(draft => draft with { FriendlyName = name.Text });

        AddPercentage("Display brightness", config.BacklightDuty, (draft, value) => draft with { BacklightDuty = value });
        AddWholeNumber("Onboard logging interval", config.LoggingInterval, "Seconds", (draft, value) => draft with { LoggingInterval = value });

        var averagingCount = WireViewSettingsValidation.AveragingOptionCount(config.Version, config.FirmwareVersion);
        if (averagingCount > 0)
        {
            var choices = DeviceConfig.AveragingOptions.Take(averagingCount)
                .Select(value => ((string?)value, value[..^2])).ToArray();

            AddChoice("Averaging (ms)", config.Averaging, (draft, value) => draft with { Averaging = value }, choices);
        }

        AddSection("Fan");
        AddChoice("Mode", config.FanMode, (draft, value) => draft with { FanMode = value },
            [("Curve", "Temperature curve"), ("Fixed", "Fixed duty")]);
        AddChoice("Temperature source", config.FanTempSource, (draft, value) => draft with { FanTempSource = value },
            [("TsIn", "Board input"), ("TsOut", "Board output"), ("Ts1", "External sensor 1"), ("Ts2", "External sensor 2"),
             ("Tmax", "Highest temperature")]);
        AddPercentage("Minimum duty", config.FanDutyMin, (draft, value) => draft with { FanDutyMin = value });
        AddPercentage("Maximum duty", config.FanDutyMax, (draft, value) => draft with { FanDutyMax = value });
        AddTemperature("Minimum temperature", config.FanTempMinC, (draft, value) => draft with { FanTempMinC = value });
        AddTemperature("Maximum temperature", config.FanTempMaxC, (draft, value) => draft with { FanTempMaxC = value });

        AddSection("Hardware protection");
        AddTemperature("Overtemperature threshold", config.TsFaultThresholdC, (draft, value) => draft with { TsFaultThresholdC = value });
        AddWholeNumber("Total overcurrent threshold", config.OcpFaultThresholdA, "Amps", (draft, value) => draft with { OcpFaultThresholdA = value });
        AddNumber("Per-pin overcurrent threshold", config.WireOcpFaultThresholdA, "Amps", 0, WireViewSettingsValidation.MaximumPinCurrentA, 1,
            (draft, value) => draft with { WireOcpFaultThresholdA = value });
        AddNumber("Overpower threshold", config.OppFaultThresholdW, "Watts", 0, ushort.MaxValue, 0,
            (draft, value) => draft with { OppFaultThresholdW = checked((ushort)value) });
        AddPercentage("Current imbalance threshold", config.CurrentImbalanceThreshold, (draft, value) => draft with { CurrentImbalanceThreshold = value });
        AddWholeNumber("Current imbalance minimum load", config.CurrentImbalanceMinLoad, "Amps",
            (draft, value) => draft with { CurrentImbalanceMinLoad = value });
        AddWholeNumber("Shutdown delay", config.ShutdownWaitTimeS, "Seconds", (draft, value) => draft with { ShutdownWaitTimeS = value });
        AddFaultActions(config);

        if (config.Display is { } display)
        {
            AddDisplay(display);
        }
    }

    public DeviceConfig Read()
    {
        var draft = _original;
        foreach (var readField in _readFields)
        {
            draft = readField(draft);
        }

        WireViewSettingsValidation.Validate(_original, draft, _original.FirmwareVersion.GetValueOrDefault());
        return draft;
    }

    private void AddDisplay(DeviceDisplayConfig display)
    {
        AddSection("Device display");
        DisplayChoice("Current scale (Amps)", display.CurrentScale, (settings, value) => settings with { CurrentScale = value },
            (0, "5"), (1, "10"), (2, "15"), (3, "20"));
        DisplayChoice("Power scale (Watts)", display.PowerScale, (settings, value) => settings with { PowerScale = value },
            (0, "Automatic"), (1, "300"), (2, "600"));
        DisplayChoice("Rotation (Degrees)", display.Rotation, (settings, value) => settings with { Rotation = value },
            (0, "0"), (1, "180"));
        DisplayChoice("Timeout action", display.TimeoutMode, (settings, value) => settings with { TimeoutMode = value },
            (0, "Static"), (1, "Cycle screens"), (2, "Sleep"));
        AddWholeNumber("Cycle interval", display.CycleTimeS, "Seconds",
            (draft, value) => draft with { Display = draft.Display! with { CycleTimeS = value } });
        AddWholeNumber("Timeout", display.TimeoutS, "Seconds",
            (draft, value) => draft with { Display = draft.Display! with { TimeoutS = value } });
        AddScreensInRotation(display.CycleScreens);

        if (display.Theme is { } theme)
        {
            DisplayChoice("Theme", theme, (settings, value) => settings with { Theme = value },
                (0, "TG1"), (1, "TG2"), (2, "TG3"));
        }

        if (display.DefaultScreen is { } screen)
        {
            DisplayChoice("Startup screen", screen, (settings, value) => settings with { DefaultScreen = value },
                ScreenNames.Select((label, index) => ((byte)index, label)).ToArray());
            DisplayChoice("Background image", display.BackgroundBitmapId!.Value, (settings, value) => settings with { BackgroundBitmapId = value },
                (1, "Thermal Grizzly Orange"), (2, "Thermal Grizzly Dark"), (255, "Disabled"));
            DisplayChoice("Fan graphic", display.FanBitmapId!.Value, (settings, value) => settings with { FanBitmapId = value },
                (0x64, "Thermal Grizzly Orange"), (0x75, "Thermal Grizzly Dark"), (0x98, "Thermal Grizzly Black and White"));
            DisplayChoice("Invert display", display.Inversion!.Value, (settings, value) => settings with { Inversion = value },
                (0, "Off"), (1, "On"));

            AddColor("Primary color", display.PrimaryColor!.Value, (settings, value) => settings with { PrimaryColor = value });
            AddColor("Secondary color", display.SecondaryColor!.Value, (settings, value) => settings with { SecondaryColor = value });
            AddColor("Highlight color", display.HighlightColor!.Value, (settings, value) => settings with { HighlightColor = value });
            AddColor("Background color", display.BackgroundColor!.Value, (settings, value) => settings with { BackgroundColor = value });
        }
    }

    private void AddSection(string name)
    {
        _section = new Grid { ColumnSpacing = 16, RowSpacing = 10 };
        _section.ColumnDefinitions.Add(new ColumnDefinition());
        _section.ColumnDefinitions.Add(new ColumnDefinition());
        Content.Children.Add(new SectionPanel { Header = name, Body = _section });
    }

    private void AddField(string label, FrameworkElement input)
    {
        var row = AddRow(_section);
        PlaceCell(_section, new TextBlock
        {
            Text = label,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        }, row, 0);

        input.HorizontalAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(input, label);
        PlaceCell(_section, input, row, 1);
    }

    private void AddPercentage(string label, byte current, Func<DeviceConfig, byte, DeviceConfig> set)
    {
        var input = new Slider
        {
            Minimum = 0,
            Maximum = WireViewSettingsValidation.MaximumPercentage,
            StepFrequency = 1,
            SmallChange = 1,
            LargeChange = 10,
            SnapsTo = SliderSnapsTo.StepValues,
            Value = current
        };
        var readout = new TextBlock { Text = $"{current}", VerticalAlignment = VerticalAlignment.Center };
        var changed = false;

        input.ValueChanged += (_, args) =>
        {
            changed = true;
            readout.Text = $"{args.NewValue:0}";
        };

        var selector = new Grid { ColumnSpacing = 8 };
        selector.ColumnDefinitions.Add(new ColumnDefinition());
        selector.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        PlaceCell(selector, input, 0, 0);
        PlaceCell(selector, readout, 0, 1);

        var labelWithUnit = $"{label} (%)";
        AutomationProperties.SetName(input, labelWithUnit);
        AddField(labelWithUnit, selector);
        _readFields.Add(draft =>
        {
            if (!changed)
            {
                return draft;
            }

            if (!WireViewSettingsValidation.IsNumberValid(input.Value, 0, WireViewSettingsValidation.MaximumPercentage, 0))
            {
                throw new ArgumentException($"{label} must be a whole percentage from 0 to 100.");
            }

            return set(draft, checked((byte)input.Value));
        });
    }

    private void AddTemperature(string label, double current, Func<DeviceConfig, double, DeviceConfig> set) =>
        AddNumber(label, current, "°C", WireViewSettingsValidation.MinimumTemperatureC, WireViewSettingsValidation.MaximumTemperatureC, 1, set);

    private void AddWholeNumber(string label, byte current, string unit, Func<DeviceConfig, byte, DeviceConfig> set) =>
        AddNumber(label, current, unit, 0, byte.MaxValue, 0, (draft, value) => set(draft, checked((byte)value)));

    private void AddNumber(string label, double current, string unit, double minimum, double maximum, int decimalPlaces,
        Func<DeviceConfig, double, DeviceConfig> set)
    {
        var labelWithUnit = $"{label} ({unit})";
        var input = new WireViewNumberInput(labelWithUnit, current, minimum, maximum, decimalPlaces);
        AddField(labelWithUnit, input.Control);
        _readFields.Add(draft => set(draft, input.Read()));
    }

    private void AddChoice<T>(string label, T current, Func<DeviceConfig, T, DeviceConfig> set, (T Value, string Label)[] options)
    {
        var choices = options.ToList();
        var selected = choices.FindIndex(choice => EqualityComparer<T>.Default.Equals(choice.Value, current));
        if (selected < 0)
        {
            selected = choices.Count;
            choices.Add((current, current is null ? "Unavailable" : $"Current value ({current})"));
        }

        var input = new ComboBox { ItemsSource = choices.Select(choice => choice.Label).ToArray(), SelectedIndex = selected };
        AddField(label, input);
        _readFields.Add(draft => input.SelectedIndex >= 0
            ? set(draft, choices[input.SelectedIndex].Value)
            : throw new ArgumentException($"Select a value for {label}."));
    }

    private void DisplayChoice(string label, byte current, Func<DeviceDisplayConfig, byte, DeviceDisplayConfig> set,
        params (byte Value, string Label)[] choices) =>
        AddChoice(label, current, (draft, value) => draft with { Display = set(draft.Display!, value) }, choices);

    private void AddColor(string label, uint current, Func<DeviceDisplayConfig, uint, DeviceDisplayConfig> set)
    {
        var initial = Color.FromArgb((byte)(current >> 24), (byte)(current >> 16), (byte)(current >> 8), (byte)current);
        var picker = new ColorPicker
        {
            Color = initial,
            IsAlphaEnabled = true,
            IsAlphaSliderVisible = true,
            IsColorChannelTextInputVisible = false,
            IsHexInputVisible = false
        };
        var brush = new SolidColorBrush(initial);
        var swatch = new Border { Width = 22, Height = 18, CornerRadius = new CornerRadius(3), Background = brush,
            BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray) };
        var value = new TextBlock { Text = $"#{current:X8}", VerticalAlignment = VerticalAlignment.Center };
        var preview = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        preview.Children.Add(swatch);
        preview.Children.Add(value);

        var input = new Button { Content = preview, Flyout = new Flyout { Content = picker } };
        picker.ColorChanged += (_, args) =>
        {
            brush.Color = args.NewColor;
            value.Text = $"#{PackColor(args.NewColor):X8}";
        };

        AddField(label, input);
        _readFields.Add(draft => draft with { Display = set(draft.Display!, PackColor(picker.Color)) });
    }

    private static uint PackColor(Color color) => (uint)color.A << 24 | (uint)color.R << 16 | (uint)color.G << 8 | color.B;

    private void AddFaultActions(DeviceConfig config)
    {
        (string Label, ushort Mask, Func<DeviceConfig, ushort, DeviceConfig> Set)[] actions =
        [
            ("Warning", config.FaultDisplayEnable, (draft, value) => draft with { FaultDisplayEnable = value }),
            ("Buzzer", config.FaultBuzzerEnable, (draft, value) => draft with { FaultBuzzerEnable = value }),
            ("Soft shutdown", config.FaultSoftPowerEnable, (draft, value) => draft with { FaultSoftPowerEnable = value }),
            ("Hard shutdown", config.FaultHardPowerEnable, (draft, value) => draft with { FaultHardPowerEnable = value })
        ];

        var table = new Grid { ColumnSpacing = 8, RowSpacing = 4 };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        var header = AddRow(table);
        AddTableHeading(table, "Setting", header, 0);

        var checks = new CheckBox[actions.Length][];
        for (var action = 0; action < actions.Length; action++)
        {
            table.ColumnDefinitions.Add(new ColumnDefinition());
            AddTableHeading(table, actions[action].Label, header, action + 1);
            checks[action] = new CheckBox[FaultNames.Length];
        }

        for (var fault = 0; fault < FaultNames.Length; fault++)
        {
            var row = AddRow(table);
            PlaceCell(table, new TextBlock
            {
                Text = FaultNames[fault],
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            }, row, 0);

            for (var action = 0; action < actions.Length; action++)
            {
                var check = new CheckBox
                {
                    IsChecked = (actions[action].Mask & (1 << fault)) != 0,
                    MinWidth = 0,
                    Padding = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                AutomationProperties.SetName(check, $"{FaultNames[fault]}: {actions[action].Label}");
                checks[action][fault] = check;
                PlaceCell(table, check, row, action + 1);
            }
        }

        _readFields.Add(draft =>
        {
            for (var action = 0; action < actions.Length; action++)
            {
                draft = actions[action].Set(draft, ReadMask(actions[action].Mask, checks[action]));
            }

            return draft;
        });

        Content.Children.Add(new SectionPanel { Header = "Fault actions", Body = table });
    }

    private void AddScreensInRotation(byte current)
    {
        var options = new StackPanel();
        var checks = new CheckBox[ScreenNames.Length];

        for (var index = 0; index < ScreenNames.Length; index++)
        {
            var check = new CheckBox { Content = ScreenNames[index], IsChecked = (current & (1 << index)) != 0 };
            checks[index] = check;
            options.Children.Add(check);
        }

        AddField("Screens in rotation", options);
        _readFields.Add(draft => draft with { Display = draft.Display! with { CycleScreens = (byte)ReadMask(current, checks) } });
    }

    private static ushort ReadMask(ushort original, IReadOnlyList<CheckBox> checks)
    {
        // Keep bits that the editor does not expose.
        var mask = original;
        for (var index = 0; index < checks.Count; index++)
        {
            var bit = 1 << index;
            mask = (ushort)(checks[index].IsChecked == true ? mask | bit : mask & ~bit);
        }

        return mask;
    }

    private static void AddTableHeading(Grid table, string text, int row, int column)
    {
        PlaceCell(table, new TextBlock
        {
            Text = text,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = column == 0 ? TextAlignment.Left : TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }, row, column);
    }

    private static int AddRow(Grid grid)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        return row;
    }

    private static void PlaceCell(Grid grid, FrameworkElement cell, int row, int column)
    {
        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }
}
