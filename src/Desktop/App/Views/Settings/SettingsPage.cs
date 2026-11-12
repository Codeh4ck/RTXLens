using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RTXLens.App.Themes;
using RTXLens.App.ViewModels.Shell;
using RTXLens.App.Views.Shell;
using RTXLens.Presentation.Settings;
using RTXLens.Presentation.Updates;
using static RTXLens.App.Views.Shell.DesktopControls;

namespace RTXLens.App.Views.Settings;

internal sealed class SettingsPage : UserControl
{
    private static readonly (string Id, string Label)[] StartupTabs =
    [
        ("", "First reporting plugin"), ("wireview-pro2", "WireView Pro II"), ("asus-astral", "ROG Astral"),
        ("generic-nvidia", "GPU Status"), ("msi-psu", "MSI PSU"), ("performance", "Performance Chart"), ("event-log", "Event Log")
    ];

    private readonly ShellViewModel _shell;
    private readonly List<Func<AppPreferences, AppPreferences>> _preferenceEdits = [];
    private readonly List<Action<ThemePalette>> _paletteFields = [];
    private readonly Button _apply;
    private readonly TabControl _tabs;
    private readonly ContentControl _feedback;
    private readonly DispatcherTimer _feedbackTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    private AppPreferences _baseline;
    private PaletteSelection _savedPalette;
    private PaletteSelection _draftPalette;
    private TextBox? _serviceAddress;
    private bool _refreshingPalette;
    private bool _applying;

    public SettingsPage(ShellViewModel shell)
    {
        Margin = new Thickness(0, 0, -PageScrollbarGutter, 0);
        _shell = shell;
        _baseline = shell.Preferences;
        _savedPalette = _draftPalette = shell.Appearance.SelectedPalette;

        _apply = CommitButton("Apply", async void () => await ApplyAsync());
        _apply.IsEnabled = false;
        _feedback = Alert(dismiss: DismissFeedback);
        _feedback.Margin = new Thickness(0, 0, PageScrollbarGutter, 0);
        _feedbackTimer.Tick += (_, _) => DismissFeedback();
        DetachedFromVisualTree += (_, _) => _feedbackTimer.Stop();

        _tabs = new TabControl
        {
            TabStripPlacement = Dock.Left,
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Spacing = 4 }),
            Theme = (ControlTheme)Application.Current!.FindResource("VerticalConfigurationTabStripTheme")!
        };
        AutomationProperties.SetName(_tabs, "Settings sections");
        _tabs.SelectionChanged += (_, args) =>
        {
            if (ReferenceEquals(args.Source, _tabs) && _tabs.SelectedItem is TabItem { Tag: Func<Control> create } selected)
            {
                selected.Tag = null;
                selected.Content = Scroll(new Border
                {
                    Padding = new Thickness(0, 0, PageScrollbarGutter, 8),
                    Child = create()
                });
            }
        };
        _tabs.ItemsSource = new[]
        {
            CreateTab("Appearance", Appearance),
            CreateTab("Desktop behavior", DesktopBehavior),
            CreateTab("Software updates", SoftwareUpdates),
            CreateTab("Service connection", ServiceConnection)
        };
        _tabs.SelectedIndex = 0;

        var heading = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 12,
            Margin = new Thickness(0, 0, PageScrollbarGutter + NavigationInset, 0),
            Children =
            {
                new TextBlock { Text = "Settings", Classes = { "heading" }, VerticalAlignment = VerticalAlignment.Center },
                _apply
            }
        };
        Grid.SetColumn(_apply, 1);

        var layout = new Grid
        {
            Margin = new Thickness(0, 6, 0, 10),
            RowDefinitions = new RowDefinitions("Auto,Auto,*"),
            RowSpacing = 10,
            Children = { heading, _feedback, _tabs }
        };
        Grid.SetRow(_feedback, 1);
        Grid.SetRow(_tabs, 2);
        Content = layout;
    }

    private static TabItem CreateTab(string title, Func<Control> create) => new()
    {
        Header = title,
        Classes = { "vertical" },
        Theme = (ControlTheme)Application.Current!.FindResource("ConfigurationTabTheme")!,
        Tag = create
    };

    private Control Appearance()
    {
        var preferences = _baseline;
        var theme = Select("Color mode", ["Use Windows setting", "Dark", "Light"], preferences.Theme switch
        {
            AppTheme.Dark => 1,
            AppTheme.Light => 2,
            _ => 0
        });
        TrackPreference(current => current.Theme,
            () => theme.SelectedIndex switch { 1 => AppTheme.Dark, 2 => AppTheme.Light, _ => AppTheme.System },
            (current, value) => current with { Theme = value });
        theme.SelectionChanged += (_, _) =>
        {
            RefreshPaletteFields();
            UpdateApplyState();
        };

        var scale = new NumericUpDown
        {
            Minimum = 0.85m,
            Maximum = 1.5m,
            Increment = 0.05m,
            FormatString = "0.00",
            ClipValueToMinMax = true,
            Value = (decimal)preferences.TextScale
        };
        AutomationProperties.SetName(scale, "Text scale");
        TrackPreference(current => current.TextScale, () => (double)(scale.Value ?? (decimal)_baseline.TextScale),
            (current, value) => current with { TextScale = value });
        scale.ValueChanged += (_, _) => UpdateApplyState();

        var body = Stack(Setting("Color mode", theme), Setting("Text scale", scale),
            Toggle("Use Fahrenheit", current => current.Fahrenheit, (current, value) => current with { Fahrenheit = value }),
            Toggle("Show peak markers", current => current.ShowPeakHold, (current, value) => current with { ShowPeakHold = value }),
            Toggle("Show pin current headroom", current => current.ShowHeadroom, (current, value) => current with { ShowHeadroom = value }));

        body.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(0, 4),
            [!Border.BackgroundProperty] = new DynamicResourceExtension("PanelBorderBrush")
        });

        var presets = ThemePreset.All;
        var choices = new[] { "RTX Lens (default)" }.Concat(presets.Select(preset => preset.Name)).Append("Custom").ToArray();
        var paletteTheme = Select("Theme", choices, SelectedThemeIndex());
        _paletteFields.Add(_ => paletteTheme.SelectedIndex = SelectedThemeIndex());
        paletteTheme.SelectionChanged += (_, _) =>
        {
            var index = paletteTheme.SelectedIndex;
            if (_refreshingPalette || index < 0)
            {
                return;
            }

            _draftPalette = index switch
            {
                0 => default,
                _ when index == choices.Length - 1 => new PaletteSelection(CustomColors: DraftColors()),
                _ => new PaletteSelection(presets[index - 1])
            };
            RefreshPaletteFields();
            UpdateApplyState();
        };
        body.Children.Add(Setting("Theme", paletteTheme));

        AddColor("Primary accent", palette => palette.Primary, (palette, color) => palette with { Primary = color });
        AddColor("Secondary", palette => palette.Secondary, (palette, color) => palette with { Secondary = color });
        AddColor("Tertiary", palette => palette.Tertiary, (palette, color) => palette with { Tertiary = color });
        AddColor("Background", palette => palette.Background, (palette, color) => palette with { Background = color });

        var reset = ActionButton("Reset colors", "refresh", () =>
        {
            _draftPalette = default;
            RefreshPaletteFields();
            UpdateApplyState();
        });
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        body.Children.Add(reset);
        RefreshPaletteFields();

        return Section("Appearance", body);

        int SelectedThemeIndex()
        {
            if (_draftPalette.Preset is { } preset)
            {
                return Array.IndexOf(choices, preset.Name);
            }

            return _draftPalette.IsDefault ? 0 : choices.Length - 1;
        }

        void AddColor(string title, Func<ThemePalette, Color> read, Func<ThemePalette, Color, ThemePalette> update)
        {
            var picker = new ColorPicker { IsAlphaEnabled = false, HorizontalAlignment = HorizontalAlignment.Right };
            AutomationProperties.SetName(picker, title);
            _paletteFields.Add(palette => picker.Color = read(palette));
            picker.PropertyChanged += (_, args) =>
            {
                if (!_refreshingPalette && args.Property == ColorPicker.ColorProperty)
                {
                    var colors = update(DraftColors(), picker.Color);
                    _draftPalette = new PaletteSelection(CustomColors: colors);
                    RefreshPaletteFields();
                    UpdateApplyState();
                }
            };
            body.Children.Add(Setting(title, picker));
        }
    }

    private Control DesktopBehavior()
    {
        var preferences = _baseline;
        var startupTab = Select("Startup tab", [.. StartupTabs.Select(tab => tab.Label)],
            Math.Max(0, Array.FindIndex(StartupTabs, tab => tab.Id == preferences.StartupTabId)));
        TrackPreference(current => current.StartupTabId,
            () => startupTab.SelectedIndex >= 0 ? StartupTabs[startupTab.SelectedIndex].Id : _baseline.StartupTabId,
            (current, value) => current with { StartupTabId = value });
        startupTab.SelectionChanged += (_, _) => UpdateApplyState();

        var startWithWindows = Toggle("Start with Windows", current => current.StartWithWindows,
            (current, value) => current with { StartWithWindows = value });
        startWithWindows.IsEnabled = ShellViewModel.CanRegisterStartup;

        var items = Stack(Setting("Startup tab", startupTab),
            Caption("If the selected plugin is unavailable, RTX Lens opens the first reporting plugin."),
            Toggle("Remember window size", current => current.RememberWindowSize,
                (current, value) => current with { RememberWindowSize = value }),
            Toggle("Remember window position", current => current.RememberWindowPosition,
                (current, value) => current with { RememberWindowPosition = value }),
            Toggle("Minimize to system tray", current => current.MinimizeToTray,
                (current, value) => current with { MinimizeToTray = value }),
            Toggle("Close to system tray", current => current.CloseToTray, (current, value) => current with { CloseToTray = value }),
            Toggle("Confirm before exiting", current => !current.SuppressExitConfirmation,
                (current, value) => current with { SuppressExitConfirmation = !value }),
            startWithWindows,
            Caption(startWithWindows.IsEnabled ? "Keep the installation in the same location when using startup registration."
                : "Startup registration is available in the administrator-run release app."),
            Toggle("Start minimized", current => current.StartMinimized, (current, value) => current with { StartMinimized = value }),
            Toggle("Windows warning notifications on this PC", current => current.WarningNotifications,
                (current, value) => current with { WarningNotifications = value }),
            Caption("Configure shared warning thresholds and reminders under Configuration > Warnings. This switch controls this PC."));
        return Section("Desktop behavior", items);
    }

    private Control SoftwareUpdates()
    {
        var updates = _shell.Updates;
        var automatic = Toggle("Automatically check for updates", current => current.AutomaticallyCheckForUpdates,
            (current, value) => current with { AutomaticallyCheckForUpdates = value });
        var frequency = Select("Check frequency", ["Daily", "Weekly", "Monthly"], (int)_baseline.UpdateFrequency);
        TrackPreference(current => current.UpdateFrequency,
            () => frequency.SelectedIndex >= 0 ? (UpdateFrequency)frequency.SelectedIndex : _baseline.UpdateFrequency,
            (current, value) => current with { UpdateFrequency = value });
        frequency.SelectionChanged += (_, _) => UpdateApplyState();

        var installed = new TextBlock();
        BindTo(installed, TextBlock.TextProperty, updates, nameof(updates.ServiceVersion));
        var status = Caption();
        BindTo(status, TextBlock.TextProperty, updates, nameof(updates.Status));
        var checkedAt = new TextBlock();
        BindTo(checkedAt, TextBlock.TextProperty, updates, nameof(updates.LastChecked));
        var check = ActionButton("Check now", "refresh", async void () => await updates.CheckAsync(true, _shell.StopToken));
        BindTo(check, IsEnabledProperty, updates, "!Checking");

        var notices = new ItemsControl
        {
            ItemsSource = updates.Available,
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Spacing = 8 }),
            ItemTemplate = new FuncDataTemplate<UpdateNotice>((notice, _) => notice is null ? null : UpdateCard(notice))
        };
        return Section("Software updates", Stack(automatic, Setting("Check frequency", frequency),
            Caption("Automatic checks run on startup and at the selected interval. Remind me later postpones the notice for 24 hours."),
            Setting("Desktop app", new TextBlock { Text = updates.DesktopVersion }), Setting("Backend service", installed),
            status, Setting("Last checked", checkedAt), check, notices));
    }

    private Control UpdateCard(UpdateNotice notice)
    {
        var release = ActionButton("View release", "chart-export",
            async void () => await _shell.RunAsync(async () =>
            {
                if (TopLevel.GetTopLevel(this) is { } root && !await root.Launcher.LaunchUriAsync(new Uri(notice.ReleaseUrl)))
                {
                    throw new IOException("The release page could not be opened.");
                }
            }));
        var notes = ActionButton("View release notes", "info-action", async void () =>
        {
            if (TopLevel.GetTopLevel(this) is Window owner)
            {
                await _shell.RunAsync(() => ReleaseNotesWindow.ShowAsync(owner, _shell.StopToken, notice.ReleaseTag, notice.ReleaseUrl));
            }
        });

        var actions = new WrapPanel
        {
            ItemSpacing = 8,
            LineSpacing = 8,
            Children =
            {
                release, notes,
                ActionButton("Remind me later", "timer",
                    async void () => await _shell.RunAsync(() => _shell.Updates.DismissAsync(notice, false, _shell.StopToken))),
                ActionButton("Skip this version", "chart-forward",
                    async void () => await _shell.RunAsync(() => _shell.Updates.DismissAsync(notice, true, _shell.StopToken)))
            }
        };
        return Section(notice.Description, actions);
    }

    private Control ServiceConnection()
    {
        var address = _serviceAddress = new TextBox { Text = _baseline.ServiceAddress };
        AutomationProperties.SetName(address, "Service address");
        var reconnect = ActionButton("Reconnect", "refresh", _shell.Reconnect);
        reconnect.HorizontalAlignment = HorizontalAlignment.Left;
        TrackPreference(current => current.ServiceAddress, () => address.Text?.Trim() ?? string.Empty,
            (current, value) => current with { ServiceAddress = value });
        address.TextChanged += (_, _) =>
        {
            reconnect.IsEnabled = string.Equals(address.Text?.Trim(), _baseline.ServiceAddress, StringComparison.Ordinal);
            UpdateApplyState();
        };
        return Section("Service connection", Stack(Setting("Service address", address),
            Caption("Apply to connect to a new address."), reconnect));
    }

    private Control Toggle(string title, Func<AppPreferences, bool> read, Func<AppPreferences, bool, AppPreferences> update)
    {
        var toggle = new ToggleSwitch
        {
            IsChecked = read(_baseline),
            OnContent = null,
            OffContent = null,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        AutomationProperties.SetName(toggle, title);
        TrackPreference(read, () => toggle.IsChecked == true, update);
        toggle.IsCheckedChanged += (_, _) => UpdateApplyState();
        return Setting(title, toggle);
    }

    private void TrackPreference<T>(Func<AppPreferences, T> read, Func<T> input, Func<AppPreferences, T, AppPreferences> update)
    {
        _preferenceEdits.Add(current =>
        {
            var value = input();
            return EqualityComparer<T>.Default.Equals(read(_baseline), value) ? current : update(current, value);
        });
    }

    private AppPreferences BuildPreferences(AppPreferences current)
    {
        foreach (var edit in _preferenceEdits)
        {
            current = edit(current);
        }

        return current;
    }

    private ThemePalette DraftColors() => _shell.Appearance.GetPalette(_draftPalette, BuildPreferences(_baseline).Theme);

    private void RefreshPaletteFields()
    {
        var colors = DraftColors();
        _refreshingPalette = true;
        foreach (var refresh in _paletteFields)
        {
            refresh(colors);
        }

        _refreshingPalette = false;
    }

    private void UpdateApplyState() => _apply.IsEnabled =
        !_applying && (_savedPalette != _draftPalette || BuildPreferences(_baseline) != _baseline);

    private async Task ApplyAsync()
    {
        var draft = BuildPreferences(_baseline);
        var preferencesChanged = draft != _baseline;
        var paletteChanged = _savedPalette != _draftPalette;

        _applying = true;
        _tabs.IsEnabled = false;
        UpdateApplyState();
        DismissFeedback();

        try
        {
            if (preferencesChanged)
            {
                if (!await _shell.ChangePreferencesAsync(BuildPreferences))
                {
                    SetAlert(_feedback, _shell.OperationStatus, "error");
                    return;
                }

                var addressChanged = _baseline.ServiceAddress != draft.ServiceAddress;
                _baseline = draft;
                if (addressChanged && _serviceAddress is { } address)
                {
                    _baseline = _baseline with { ServiceAddress = _shell.Preferences.ServiceAddress };
                    address.Text = _baseline.ServiceAddress;
                }
            }

            if (paletteChanged)
            {
                await _shell.Appearance.ApplyPaletteAsync(_draftPalette);
                _savedPalette = _draftPalette;
            }

            SetAlert(_feedback, "Your settings have been applied.", "success");
            if (this.IsAttachedToVisualTree())
            {
                _feedbackTimer.Start();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SetAlert(_feedback, preferencesChanged
                ? "Your settings were applied, but your colors could not be saved. Try Apply again."
                : "Your colors could not be saved. Try Apply again.", "error");
        }
        finally
        {
            _applying = false;
            _tabs.IsEnabled = true;
            UpdateApplyState();
        }
    }

    private void DismissFeedback()
    {
        _feedbackTimer.Stop();
        SetAlert(_feedback, null);
    }

    private static ComboBox Select(string name, string[] items, int selected)
    {
        var selector = new ComboBox { ItemsSource = items, SelectedIndex = selected, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(selector, name);
        return selector;
    }

    private static StackPanel Stack(params Control[] items)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.AddRange(items);
        return panel;
    }
}
