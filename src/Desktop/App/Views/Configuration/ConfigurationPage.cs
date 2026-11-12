using System.Collections.Specialized;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using RTXLens.App.Controls;
using RTXLens.App.ViewModels.Shell;
using RTXLens.Presentation.Configuration;
using RTXLens.Presentation.Telemetry;
using static RTXLens.App.Views.Shell.DesktopControls;

namespace RTXLens.App.Views.Configuration;

internal sealed class ConfigurationPage : UserControl
{
    private readonly ShellViewModel _shell;
    private readonly ConfigurationEditorViewModel _editor;
    private readonly TabControl _tabs = new()
    {
        ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 })
    };

    public ConfigurationPage(ShellViewModel shell)
    {
        // Keep the scrollbar in the shell's right margin so the content stays aligned with the navigation.
        Margin = new Thickness(0, 0, -PageScrollbarGutter, 0);
        _shell = shell;
        _editor = shell.Configuration;

        var revision = Caption();
        BindTo(revision, TextBlock.TextProperty, _editor, nameof(_editor.RevisionText));

        var message = Alert();
        var success = Alert(tone: "success", dismiss: _editor.DismissApplyMessage);

        var feedback = new Panel
        {
            IsVisible = false,
            Margin = new Thickness(0, 0, PageScrollbarGutter, 0),
            Children = { message, success }
        };

        var expiry = new DispatcherTimer();
        expiry.Tick += (_, _) =>
        {
            expiry.Stop();
            _editor.DismissApplyMessage();
        };

        Watch(feedback, _editor, UpdateMessage, nameof(_editor.Message));
        Watch(feedback, _editor, UpdateMessage, nameof(_editor.MessageTone));
        Watch(feedback, _editor, UpdateMessage, nameof(_editor.HasExternalChanges));

        var reload = ActionButton("Reload", "refresh", async void () => await _editor.ReloadAsync(shell.StopToken));
        BindTo(reload, IsEnabledProperty, _editor, "!IsBusy");

        var apply = CommitButton("Apply", async void () =>
        {
            var previous = _editor.CurrentConfiguration;
            await _editor.ApplyAsync(shell.StopToken);
            if (!shell.IsSimulation)
            {
                await shell.LocalEvents.RecordAsync("Service configuration", previous, _editor.CurrentConfiguration);
            }
            shell.RefreshPresentation(force: true);
        });

        Watch(apply, _editor, () => apply.IsEnabled = _editor is { IsDirty: true, CanEdit: true, HasExternalChanges: false });

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };

        actions.Children.Add(revision);
        actions.Children.Add(reload);
        actions.Children.Add(apply);

        revision.VerticalAlignment = VerticalAlignment.Center;
        var heading = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 12,
            Margin = new Thickness(0, 0, PageScrollbarGutter + NavigationInset, 0),
            Children =
            {
                new TextBlock { Text = "Configuration", Classes = { "heading" }, VerticalAlignment = VerticalAlignment.Center },
                actions
            }
        };

        Grid.SetColumn(actions, 1);

        _tabs.Padding = new Thickness(0, 0, PageScrollbarGutter, 0);
        _tabs.Theme = (ControlTheme)Application.Current!.FindResource("ConfigurationTabStripTheme")!;

        _tabs.SelectionChanged += (_, args) =>
        {
            if (ReferenceEquals(args.Source, _tabs))
            {
                RealizeSelectedTab();
            }
        };

        var layout = new Grid
        {
            Margin = new Thickness(0, 12, 0, 12),
            RowDefinitions = new RowDefinitions("Auto,Auto,*"),
            RowSpacing = 10,
            Children = { heading, feedback, _tabs }
        };

        Grid.SetRow(feedback, 1);
        Grid.SetRow(_tabs, 2);

        Content = layout;

        var discovery = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        discovery.Tick += async (_, _) => await _shell.RefreshDeviceListsAsync();

        AttachedToVisualTree += async (_, _) =>
        {
            _editor.Plugins.CollectionChanged += PluginsChanged;
            PopulateTabs();
            discovery.Start();
            await _shell.RefreshDeviceListsAsync();
        };

        DetachedFromVisualTree += (_, _) =>
        {
            discovery.Stop();
            expiry.Stop();
            _editor.Plugins.CollectionChanged -= PluginsChanged;
        };
        return;

        void UpdateMessage()
        {
            expiry.Stop();
            if (_editor.ApplyMessageExpiresAt is { } expiresAt)
            {
                var remaining = expiresAt - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    _editor.DismissApplyMessage();
                    return;
                }

                expiry.Interval = remaining;
                expiry.Start();
            }

            var showSuccess = _editor is { HasExternalChanges: false, MessageTone: StatusTone.Positive };
            SetAlert(success, showSuccess ? _editor.Message : null, "success");
            if (_editor.HasExternalChanges)
            {
                SetAlert(message, "Configuration changed elsewhere. Reload before applying.", "warning");
                feedback.IsVisible = true;
                return;
            }

            var tone = _editor.MessageTone switch
            {
                StatusTone.Caution => "warning",
                StatusTone.Negative => "error",
                _ => "info"
            };

            SetAlert(message, showSuccess ? null : _editor.Message, tone);
            feedback.IsVisible = message.IsVisible || success.IsVisible;
        }
    }

    private bool _refreshQueued;

    private void PluginsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            if (TopLevel.GetTopLevel(this) is not null)
            {
                PopulateTabs();
            }
        });
    }

    private void PopulateTabs()
    {
        var previous = (_tabs.SelectedItem as TabItem)?.Header as string;

        var tabs = (from plugin in _editor.Plugins.OrderBy(plugin => plugin.IsNvidia ? 0 : plugin.IsWireView ? 1 : plugin.IsAsus ? 2 : 3)
            let label = plugin.IsNvidia ? "Nvidia"
                : plugin.IsWireView ? "WireView Pro II"
                : plugin.IsAsus ? "ROG Astral"
                : plugin.IsMsi ? "MSI PSU" : plugin.Name
            select Tab(label, () => PluginSettings(plugin))).ToList();

        tabs.Add(Tab("Streaming", CreateStreaming));

        var warnings = _tabs.Items.OfType<TabItem>().FirstOrDefault(tab => Equals(tab.Header, "Warnings"));

        tabs.Add(warnings ?? Tab("Warnings", () => new WarningsConfigurationView(_shell)));
        tabs.Add(Tab("GPU chart profiles", () => new GpuChartProfilesView(_shell)));
        tabs.Add(Tab("Source selections", CreateSources));
        tabs.Add(Tab("Maintenance", CreateMaintenance));

        _tabs.ItemsSource = tabs;
        _tabs.SelectedItem = tabs.FirstOrDefault(tab => Equals(tab.Header, previous)) ?? tabs[0];

        RealizeSelectedTab();
    }

    private static TabItem Tab(string header, Func<Control> factory) => new()
    {
        Header = header,
        Theme = (ControlTheme)Application.Current!.FindResource("ConfigurationTabTheme")!,
        Tag = factory
    };

    private void RealizeSelectedTab()
    {
        if (_tabs.SelectedItem is TabItem { Tag: Func<Control> create } tab)
        {
            tab.Tag = null;
            tab.Content = Scroll(new Border { Padding = new Thickness(0, 0, PageScrollbarGutter, 8), Child = create() });
        }
    }

    private Control PluginSettings(PluginSettingsEditorViewModel plugin)
    {
        var fields = new StackPanel { Spacing = 14 };
        BindTo(fields, IsEnabledProperty, _editor, nameof(_editor.CanEdit));

        fields.Children.Add(Caption("Updates affect the service and every connected consumer."));

        fields.Children.Add(Columns(
            NumberField("Polling interval (ms)", plugin, nameof(plugin.PollIntervalMs), plugin.MinimumPollMs, 30000, 100),
            NumberField("Stale after (ms)", plugin, nameof(plugin.StaleAfterMs), 100, 300000, 100)));

        var enabled = Switch("Enabled", plugin, nameof(plugin.Enabled));
        BindTo(enabled, IsEnabledProperty, _editor, nameof(_editor.CanEdit));

        var section = Section(plugin.Name, fields);
        section.HeaderActions = enabled;

        if (plugin.IsNvidia)
        {
            var nvidia = plugin.Nvidia;
            var device = Choice(nvidia.Devices, nvidia, nameof(nvidia.SelectedDevice), item => item.Name);

            section.HeaderActions = DeviceActions(plugin, enabled, device, "GPU");

            var statusTab = Switch("Show GPU status as a separate tab");
            statusTab.IsChecked = !_shell.Dashboard.HasAlternativeGpuStatus || _shell.Preferences.ShowSeparateGpuStatusTab;
            statusTab.IsEnabled = _shell.Dashboard.HasAlternativeGpuStatus;

            statusTab.IsCheckedChanged += async (_, _) =>
            {
                _shell.SetPreferences(_shell.Preferences with { ShowSeparateGpuStatusTab = statusTab.IsChecked == true });
                await _shell.SavePreferencesAsync();
            };

            fields.Children.Add(statusTab);
        }
        else if (plugin.IsAsus)
        {
            var asus = plugin.Asus;
            var device = Choice(asus.Devices, asus, nameof(asus.SelectedDevice), item => item.Name);

            section.HeaderActions = DeviceActions(plugin, enabled, device, "GPU");
        }
        else if (plugin.IsMsi)
        {
            var msi = plugin.Msi;
            var device = Choice(msi.Devices, msi, nameof(msi.SelectedDevice), item => item.Name);
            var actions = DeviceActions(plugin, enabled, device, "PSU");

            if (OperatingSystem.IsWindows())
            {
                actions.Children.Insert(0, CreateAfterburnerAction(plugin));
            }

            section.HeaderActions = actions;
            fields.Children.Add(FanModeRow(msi));
        }

        if (!plugin.IsMsi)
        {
            return section;
        }

        return new StackPanel { Spacing = 14, Children = { section, CreatePsuComparison() } };
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private Button CreateAfterburnerAction(PluginSettingsEditorViewModel plugin)
    {
        var installed = false;
        var action = ActionButton("Afterburner Safeguard+", "settings-action", () =>
        {
            if (!_shell.IsSimulation && TopLevel.GetTopLevel(this) is Window owner)
            {
                var open = owner.OwnedWindows.OfType<Safeguard.AfterburnerSettingsWindow>().FirstOrDefault();
                if (open is null)
                {
                    var window = new Safeguard.AfterburnerSettingsWindow(_shell);
                    _shell.TrackToolWindow(window, "afterburner-settings", owner);

                    window.Show(owner);
                }
                else
                {
                    open.Activate();
                }
            }
        });

        action.IsEnabled = false;
        action.AttachedToVisualTree += async (_, _) =>
        {
            installed = (await Task.Run(Safeguard.AfterburnerSettingsWindow.FindInstallations)).Length > 0;
            UpdateAvailability();
        };

        Watch(action, plugin, UpdateAvailability, nameof(plugin.Enabled));
        Watch(action, _shell, UpdateAvailability, nameof(_shell.IsSimulation));

        return action;

        void UpdateAvailability() => action.IsEnabled = installed && plugin.Enabled && !_shell.IsSimulation;
    }

    private StackPanel DeviceActions(PluginSettingsEditorViewModel plugin, ToggleSwitch enabled, ComboBox device, string kind)
    {
        var refreshButton = IconButton($"Refresh {kind} list", "refresh",
            async void () =>
            {
                try
                {
                    await _shell.RunAsync(() => _shell.RefreshDeviceListAsync(plugin));
                }
                catch (Exception)
                {
                    // ignored
                }
            });

        Watch(refreshButton, plugin, UpdateRefreshAvailability, nameof(plugin.Enabled));
        Watch(refreshButton, _editor, UpdateRefreshAvailability, nameof(_editor.CanEdit));
        Watch(refreshButton, _shell, UpdateRefreshAvailability, nameof(_shell.IsBusy));

        device.Width = 290;
        device.VerticalAlignment = VerticalAlignment.Center;

        AutomationProperties.SetName(device, $"Selected {kind}");

        device.SelectionChanged += (_, _) => ToolTip.SetTip(device, device.SelectedItem switch
        {
            GpuChoice gpu => gpu.Name,
            PsuChoice psu => psu.Name,
            _ => $"Selected {kind}"
        });

        BindTo(device, IsEnabledProperty, _editor, nameof(_editor.CanEdit));

        enabled.VerticalAlignment = VerticalAlignment.Center;

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { refreshButton, device, enabled }
        };

        void UpdateRefreshAvailability() => refreshButton.IsEnabled = plugin.Enabled && _editor.CanEdit && !_shell.IsBusy;
    }

    private Control FanModeRow(MsiSettingsEditorViewModel msi)
    {
        var mode = new ComboBox { ItemsSource = new[] { "Unmanaged", "Automatic", "Fixed duty", "Fan curve" } };
        BindTo(mode, SelectingItemsControl.SelectedIndexProperty, msi, nameof(msi.FanModeIndex), BindingMode.TwoWay);

        var duty = NumberField("Fan duty (%)", msi, nameof(msi.FanDutyPercent), 0, 100);
        duty.Width = 140;

        BindTo(duty, IsVisibleProperty, msi, nameof(msi.IsManual));

        var curve = ActionButton("Edit fan curve", "fan-action", async void () =>
        {
            try
            {
                if (TopLevel.GetTopLevel(this) is Window owner)
                {
                    var window = new FanCurveWindow(msi, _shell);
                    _shell.TrackToolWindow(window, "msi-fan-curve", owner);
                    await window.ShowDialog(owner);
                }
            }
            catch (Exception )
            {
                // ignored
            }
        });

        curve.Height = 38;
        curve.VerticalAlignment = VerticalAlignment.Bottom;

        BindTo(curve, IsVisibleProperty, msi, nameof(msi.IsCurve));

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("240,Auto"),
            ColumnSpacing = 12,
            Children = { Field("Fan mode", mode), duty, curve }
        };

        Grid.SetColumn(duty, 1);
        Grid.SetColumn(curve, 1);

        return row;
    }

    private Control CreatePsuComparison()
    {
        var caption = Caption("Show MSI PSU amperage below the corresponding pins. Select the PSU connector feeding each device.");
        var options = Columns(Comparison(wireView: true), Comparison(wireView: false));

        StackAtNarrowWidth(options, 750);

        return Section("PSU pin comparison", new StackPanel { Spacing = 10, Children = { caption, options } });

        StackPanel Comparison(bool wireView)
        {
            var preferences = _shell.Preferences;
            var name = wireView ? "WireView Pro II" : "ROG Astral";
            var show = Switch($"Show MSI current under {name} pins");

            show.IsChecked = wireView ? preferences.ShowWireViewPsuCurrent : preferences.ShowAsusPsuCurrent;

            var connector = new ComboBox
            {
                ItemsSource = new[] { "Connector 1", "Connector 2" },
                SelectedIndex = (wireView ? preferences.WireViewPsuConnector : preferences.AsusPsuConnector) - 1
            };

            BindTo(connector, IsEnabledProperty, show, nameof(ToggleSwitch.IsChecked));

            show.IsCheckedChanged += async (_, _) => await Save();
            connector.SelectionChanged += async (_, _) => await Save();

            return new StackPanel { Spacing = 7, Children = { show, Field($"{name} PSU connector", connector) } };

            async Task Save()
            {
                if (connector.SelectedIndex < 0)
                {
                    return;
                }

                var current = _shell.Preferences;
                var visible = show.IsChecked == true;
                var selectedConnector = connector.SelectedIndex + 1;

                _shell.SetPreferences(wireView
                    ? current with { ShowWireViewPsuCurrent = visible, WireViewPsuConnector = selectedConnector }
                    : current with { ShowAsusPsuCurrent = visible, AsusPsuConnector = selectedConnector });

                await _shell.SavePreferencesAsync();
            }
        }
    }

    private Control CreateStreaming()
    {
        var interval = NumberField("Sensor stream interval (ms)", _editor, nameof(_editor.StreamIntervalMs), 100, 30000, 100);
        var limits = Columns(
            NumberField("Event log retention (days)", _editor, nameof(_editor.EventLogRetentionDays), 1, 90),
            NumberField("Maximum log storage (MB)", _editor, nameof(_editor.EventLogStorageMb), 1, 50));

        BindTo(interval, IsEnabledProperty, _editor, nameof(_editor.CanEdit));
        BindTo(limits, IsEnabledProperty, _editor, nameof(_editor.CanEdit));

        var sensors = Section("Sensor stream", new StackPanel
        {
            Spacing = 10,
            Children = { interval, Caption("Sets how often the service publishes readings to connected apps and widgets.") }
        });
        sensors.HeaderActions = HelpButton("How the sensor stream works", "Sensor stream",
            "The sensor stream sends the latest readings to the desktop app and widget. Each plugin's polling interval controls "
            + "how often it reads the hardware.",
            "If a plugin polls every 500 ms and the stream sends every 1,000 ms, the app receives the latest reading once per second.",
            "If a plugin polls every 1,000 ms and the stream sends every 250 ms, the same reading can appear in several updates.",
            "Choose a shorter stream interval for quicker updates when plugins are polling frequently. "
            + "A longer interval reduces update traffic. Select Apply to change it live.");

        var events = Section("Event log stream", new StackPanel
        {
            Spacing = 10,
            Children = { limits, Caption("The service removes the oldest events when either retention limit is reached.") }
        });

        return new StackPanel { Spacing = 14, Children = { sensors, events } };
    }

    private Control CreateSources()
    {
        var profiles = _editor.ConnectorProfiles;
        var selected = Choice(profiles.Profiles, profiles, nameof(profiles.SelectedProfile), profile => profile.DisplayName);
        var connector = new ComboBox { ItemsSource = new[] { "Automatic", "Connector 1", "Connector 2" } };

        BindTo(connector, SelectingItemsControl.SelectedIndexProperty, profiles, "SelectedProfile.MsiConnectorIndex", BindingMode.TwoWay);

        var msi = Field("MSI PSU connector", connector);
        BindTo(msi, IsVisibleProperty, profiles, "SelectedProfile.HasMsi");

        var group = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                Columns(Field("Connector profile", selected), msi)
            }
        };

        var header = Section("Connector sources", group);

        header.HeaderActions = IconButton("How source selections work", "info-action", async void () =>
        {
            try
            {
                if (TopLevel.GetTopLevel(this) is Window owner)
                {
                    await ShowInformationAsync(owner, "Source selections", UsageGuide(
                        "Source selections control which readings feed the connector power, total current, and voltage summaries. "
                        + "Each metric has its own priority list.",
                        "Choose a connector profile. For each metric, use the arrows to move your preferred source to the top. "
                        + "Use Add source or the trash icon to change the list.",
                        "Enable fallback to use the next available reading when your preferred source is unavailable. "
                        + "With fallback off, the metric uses only the first source.",
                        "If you use MSI PSU current, choose the connector feeding your GPU. Automatic uses the detected active connector "
                        + "when only one is reporting current.",
                        "Select Apply at the top of Configuration. For example, put WireView Pro II first for voltage and Nvidia first for power "
                        + "to use those two readings together."));
                }
            }
            catch (Exception)
            {
                // ignored
            }
        });

        var profileContent = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ContentTemplate = new FuncDataTemplate<ConnectorProfileEditorViewModel>((profile, _) =>
                profile is null ? null : AdaptiveCards(width => width >= 1100 ? 3 : 1, [.. profile.Metrics.Select(CreatePriorityEditor)]))
        };

        BindTo(profileContent, ContentProperty, profiles, nameof(profiles.SelectedProfile));

        var content = new StackPanel { Spacing = 14, Children = { header, profileContent } };

        BindTo(group, IsEnabledProperty, _editor, nameof(_editor.CanEdit));
        BindTo(profileContent, IsEnabledProperty, _editor, nameof(_editor.CanEdit));

        return content;
    }

    private static Control CreatePriorityEditor(ConnectorMetricEditorViewModel metric)
    {
        var remove = new RelayCommand(metric.RemoveSource, () => SelectedIndex() >= 0);
        var moveUp = new RelayCommand(() => metric.MoveSource(-1), () => SelectedIndex() > 0);
        var moveDown = new RelayCommand(() => metric.MoveSource(1), CanMoveDown);

        var sources = new EditableList
        {
            Height = 132,
            ItemsSource = metric.Sources,
            ItemTemplate = new FuncDataTemplate<ConnectorPluginChoice>((source, _) => new TextBlock { Text = source?.Label }),
            ShowAddButton = false,
            RemoveCommand = remove,
            MoveUpCommand = moveUp,
            MoveDownCommand = moveDown
        };

        AutomationProperties.SetName(sources, $"{metric.Label} source priority");
        BindTo(sources, EditableList.SelectedItemProperty, metric, nameof(metric.SelectedSource), BindingMode.TwoWay);

        var status = Alert();
        Watch(status, metric, () =>
            SetAlert(status, metric.Resolution, metric.Resolution.StartsWith("Unavailable", StringComparison.Ordinal) ? "warning" : "info"),
            nameof(metric.Resolution));

        var candidate = Choice(metric.AvailableSources, metric, nameof(metric.Candidate), item => item.Label);
        candidate.HorizontalAlignment = HorizontalAlignment.Stretch;

        AutomationProperties.SetName(candidate, $"{metric.Label} source to add");

        var add = ActionButton("Add source", "add-action", metric.AddSource);
        add.VerticalAlignment = VerticalAlignment.Center;

        Watch(add, metric, UpdateCommands);

        var addRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8,
            Children = { candidate, add }
        };

        Grid.SetColumn(add, 1);

        var section = Section(metric.Label, new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Switch("Use the next source if unavailable", metric, nameof(metric.AllowFallback)),
                sources,
                addRow,
                status
            }
        });

        section.AttachedToVisualTree += (_, _) =>
        {
            UpdateCandidates();
            metric.Sources.CollectionChanged += SourcesChanged;
        };

        section.DetachedFromVisualTree += (_, _) => metric.Sources.CollectionChanged -= SourcesChanged;

        return section;

        int SelectedIndex() => metric.SelectedSource is null ? -1 : metric.Sources.IndexOf(metric.SelectedSource);

        bool CanMoveDown()
        {
            var index = SelectedIndex();
            return index >= 0 && index < metric.Sources.Count - 1;
        }

        void SourcesChanged(object? sender, NotifyCollectionChangedEventArgs args)
        {
            if (args.Action != NotifyCollectionChangedAction.Move)
            {
                UpdateCandidates();
            }

            UpdateCommands();
        }

        void UpdateCandidates()
        {
            var choices = metric.AvailableSources.Where(option => metric.Sources.All(source => source.Id != option.Id)).ToArray();
            var selected = metric.Candidate;
            candidate.ItemsSource = choices;
            candidate.SelectedItem = choices.Contains(selected) ? selected : null;
            candidate.PlaceholderText = choices.Length == 0 ? "No additional sources" : "Choose a source";
            candidate.IsEnabled = choices.Length > 0;
        }

        void UpdateCommands()
        {
            remove.NotifyCanExecuteChanged();
            moveUp.NotifyCanExecuteChanged();
            moveDown.NotifyCanExecuteChanged();
            add.IsEnabled = metric.Candidate is { } selected && metric.Sources.All(source => source.Id != selected.Id);
        }
    }

    private Control CreateMaintenance()
    {
        var restart = ActionButton("Restart backend service", "refresh", async void () =>
        {
            if (TopLevel.GetTopLevel(this) is Window owner && await ConfirmAsync(owner, "Restart backend service",
                    "Restart the backend service and all enabled plugins?", "Restart"))
            {
                await _shell.RestartServiceAsync();
            }
        });

        var actions = new StackPanel
        {
            Spacing = 12,
            Children = { Caption("Restart the backend service and all enabled plugins."), restart }
        };

#if TELEMETRY_DEBUG
        var debugConsole = ActionButton("Open Debug Console", "terminal-action", () =>
        {
            if (TopLevel.GetTopLevel(this) is Window owner)
            {
                _shell.OpenDebugConsole(owner);
            }
        });

        BindTo(debugConsole, IsVisibleProperty, _shell, nameof(_shell.DebugConsoleRevealed));

        actions.Children.Add(debugConsole);
#endif

        var maintenance = Section("Backend maintenance", actions);
        maintenance.HeaderActions = HelpButton("About restarting the backend service", "Restart backend service",
            "Restarting the backend also restarts every enabled plugin. Your saved settings are kept.",
            "Live readings and backend warning checks pause during the restart. The desktop app and widget reconnect when the service returns.",
            "If just one device stops reporting, try Restart plugin on its hardware tab first. Restart the backend if several devices stop "
            + "reporting or the sensor stream stays stuck.",
            "Apply any changes you want to keep before restarting. Windows restarts the installed service automatically. "
            + "If you launched the backend from a terminal, start it again there.");

        return maintenance;
    }

    private Button HelpButton(string name, string title, string introduction, params string[] steps) =>
        IconButton(name, "info-action", async void () =>
        {
            if (TopLevel.GetTopLevel(this) is Window owner)
            {
                await ShowInformationAsync(owner, title, UsageGuide(introduction, steps));
            }
        });

    internal static StackPanel NumberField(string label, object source, string property, int minimum, int maximum, int increment = 1) =>
        Field(label, NumericInput(source, property, minimum, maximum, increment));

    private static NumericUpDown NumericInput(object source, string property, int minimum, int maximum, int increment)
    {
        var input = new NumericUpDown { Minimum = minimum, Maximum = maximum, Increment = increment, FormatString = "0" };
        BindTo(input, NumericUpDown.ValueProperty, source, property, BindingMode.TwoWay);
        return input;
    }

    internal static StackPanel Field(string label, Control input)
    {
        input.HorizontalAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(input, label);
        return new StackPanel { Spacing = 6, Children = { new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, input } };
    }

    internal static ToggleSwitch Switch(string label)
    {
        var input = new ToggleSwitch { OnContent = label, OffContent = label, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(input, label);
        return input;
    }

    internal static ToggleSwitch Switch(string label, object source, string property)
    {
        var input = Switch(label);
        BindTo(input, ToggleButton.IsCheckedProperty, source, property, BindingMode.TwoWay);
        return input;
    }

    private static ComboBox Choice<T>(IEnumerable<T> values, object source, string property, Func<T, string> label) where T : class
    {
        var input = new ComboBox
        {
            ItemsSource = values,
            ItemTemplate = new FuncDataTemplate<T>((value, _) => new TextBlock
            {
                Text = value is null ? null : label(value),
                TextTrimming = TextTrimming.CharacterEllipsis
            })
        };

        BindTo(input, SelectingItemsControl.SelectedItemProperty, source, property, BindingMode.TwoWay);
        return input;
    }
}
