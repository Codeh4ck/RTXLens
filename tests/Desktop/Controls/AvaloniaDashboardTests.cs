using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RTXLens.App.Controls;
using RTXLens.App.Controls.Charts;
using RTXLens.App.Platform.Startup;
using RTXLens.App.Themes;
using RTXLens.App.ViewModels.Appearance;
using RTXLens.App.ViewModels.Shell;
using RTXLens.App.Views.Configuration;
using RTXLens.App.Views.Diagnostics;
using RTXLens.App.Views.Hardware;
using RTXLens.App.Views.Performance;
using RTXLens.App.Views.Safeguard;
using RTXLens.App.Views.Shell;
using RTXLens.Client.Http;
using RTXLens.Client.Serialization;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Diagnostics;
using RTXLens.Contracts.Discovery;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Health;
using RTXLens.Contracts.Monitoring;
using RTXLens.Contracts.Sensors;
using RTXLens.Contracts.Updates;
using RTXLens.Plugins.Msi.Contracts.Configuration;
using RTXLens.Plugins.Msi.Contracts.Safeguard;
using RTXLens.Presentation.Configuration;
using RTXLens.Presentation.Msi.Afterburner;
using RTXLens.Presentation.Performance;
using RTXLens.Presentation.Settings;
using RTXLens.SafeguardReporting;
using RTXLens.Tests.Infrastructure;
using RTXLens.UI.Tests.TestDoubles;

namespace RTXLens.UI.Tests.Controls;

internal static class AvaloniaDashboardTests
{
    public static void EditsExistingPluginConfigurationModel(string? screenshotDirectory)
    {
        using var shell = CreateShell();
        shell.Configuration.Receive(new ConfigurationSnapshot(1, new ServiceConfiguration
        {
            Plugins = new Dictionary<string, PluginSettings>
            {
                ["wireview-pro2"] = new(),
                ["generic-nvidia"] = new(),
                ["asus-astral"] = new(),
                ["msi-psu"] = new()
            },
            Monitoring = new MonitoringOptions
            {
                ConnectorProfiles = new ConnectorProfiles
                {
                    Version = 1,
                    Profiles =
                    [
                        new ConnectorSourceProfile
                        {
                            Id = "automatic",
                            Name = "Automatic",
                            Metrics =
                            [
                                new() { Metric = SensorMetric.Power, PreferredPlugins = ["generic-nvidia", "asus-astral"] },
                                new() { Metric = SensorMetric.Current, PreferredPlugins = ["generic-nvidia", "msi-psu"] },
                                new() { Metric = SensorMetric.Voltage, PreferredPlugins = ["asus-astral", "wireview-pro2"] }
                            ]
                        }
                    ]
                }
            }
        }));

        var window = new ShellWindow(shell) { Width = 1280, Height = 760, RequestedThemeVariant = ThemeVariant.Dark };
        window.Show();
        try
        {
            ClickShellAction(window, "Configuration");
            var navigation = window.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "MainNavigation");
            var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
            foreach (var (tab, kind) in new[] { ("Nvidia", "GPU"), ("ROG Astral", "GPU"), ("MSI PSU", "PSU") })
            {
                SelectTab(tab);
                var section = window.GetVisualDescendants().OfType<SectionPanel>().First();
                var header = (StackPanel)section.HeaderActions!;
                var refresh = header.Children.OfType<Button>()
                    .Single(button => AutomationProperties.GetName(button) == $"Refresh {kind} list");
                TestAssert.True(refresh is { Content: Image, IsEnabled: true }, "The plugin header should contain an icon-only refresh button.");
                TestAssert.Equal($"Selected {kind}", AutomationProperties.GetName(header.Children.OfType<ComboBox>().Single()));
                var strip = tabs.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "TabStripFrame");
                AssertAlignedHorizontally(navigation, strip, window);
                AssertAlignedHorizontally(navigation, section, window);

                var enabled = header.Children.OfType<ToggleSwitch>().Single();
                enabled.IsChecked = false;
                TestAssert.True(!refresh.IsEnabled, "Refresh should be disabled when the plugin is disabled.");
                enabled.IsChecked = true;
                TestAssert.True(refresh.IsEnabled, "Refresh should be available when the plugin is enabled again.");
            }

            var msi = shell.Configuration.Plugins.Single(plugin => plugin.IsMsi).Msi;
            var mode = window.GetVisualDescendants().OfType<ComboBox>().Single(input => AutomationProperties.GetName(input) == "Fan mode");
            mode.SelectedIndex = (int)MsiFanControl.Manual;
            Dispatcher.UIThread.RunJobs();
            TestAssert.True(window.GetVisualDescendants().OfType<NumericUpDown>()
                .Single(number => AutomationProperties.GetName(number) == "Fan duty (%)").IsEffectivelyVisible,
                "Fixed duty should show the inline duty input.");
            mode.SelectedIndex = (int)MsiFanControl.Curve;
            Dispatcher.UIThread.RunJobs();
            TestAssert.Equal((int)MsiFanControl.Curve, msi.FanModeIndex);
            TestAssert.True(window.GetVisualDescendants().OfType<Button>()
                .Single(button => AutomationProperties.GetName(button) == "Edit fan curve").IsEffectivelyVisible,
                "Curve mode should show the inline editor action.");
            AvaloniaControlsTests.Capture(window, screenshotDirectory, "configuration-msi.png");

            SelectTab("WireView Pro II");
            var inputs = window.GetVisualDescendants().OfType<NumericUpDown>().ToArray();
            TestAssert.True(inputs.Any(number => number.Value == 1000),
                $"Selected tab: {(tabs.SelectedItem as TabItem)?.Header}, content: {tabs.SelectedContent?.GetType().Name}, "
                + $"numeric inputs: {string.Join(", ", inputs.Select(number => number.Value))}");
            var input = inputs.Single(number => number.Value == 1000);
            input.SetCurrentValue(NumericUpDown.ValueProperty, 1300m);
            Dispatcher.UIThread.RunJobs();

            TestAssert.Equal(1300d, shell.Configuration.Plugins.Single(plugin => plugin.IsWireView).PollIntervalMs);
            TestAssert.True(shell.Configuration.IsDirty, "The editor should register the Avalonia setting change.");
            var apply = window.GetVisualDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Apply");
            TestAssert.True(apply.IsEnabled, "Apply should be accessible from the heading while there is a draft.");
            TestAssert.Equal(Color.Parse("#111927"), ((ISolidColorBrush)apply.Background!).Color);
            TestAssert.Equal(Color.Parse("#39EED1"), ((ISolidColorBrush)apply.BorderBrush!).Color);
            TestAssert.Equal(Color.Parse("#39EED1"), ((ISolidColorBrush)apply.Foreground!).Color);
            AvaloniaControlsTests.Capture(window, screenshotDirectory, "configuration-wireview.png");

            SelectTab("Source selections");
            var lists = window.GetVisualDescendants().OfType<EditableList>().ToArray();
            TestAssert.Equal(3, lists.Length);
            TestAssert.True(window.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text == "ROG Astral"),
                "Source priority rows should display the plugin label.");
            var profile = shell.Configuration.ConnectorProfiles.SelectedProfile!;
            var power = profile.Metrics.Single(metric => metric.Metric == SensorMetric.Power);
            var addSource = window.GetVisualDescendants().OfType<ComboBox>()
                .Single(box => AutomationProperties.GetName(box) == "Power source to add");
            TestAssert.True(addSource.SelectedItem is null, "Opening Source selections should show an unselected source dropdown.");
            TestAssert.Equal("Choose a source", addSource.PlaceholderText);

            addSource.SelectedItem = addSource.Items.OfType<ConnectorPluginChoice>()
                .Single(option => option.Label == "WireView Pro II");
            var add = window.GetVisualDescendants().OfType<Button>().First(button => AutomationProperties.GetName(button) == "Add source");
            add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            TestAssert.Equal("WireView Pro II", power.SelectedSource?.Label);
            TestAssert.Equal(3, power.Sources.Count);
            TestAssert.True(addSource.SelectedItem is null && !addSource.IsEnabled && !add.IsEnabled,
                "Adding the last available source should leave the dropdown and add button disabled.");
            TestAssert.Equal("No additional sources", addSource.PlaceholderText);
            AvaloniaControlsTests.Capture(window, screenshotDirectory, "configuration-sources.png");

            void SelectTab(string label)
            {
                tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(item => Equals(item.Header, label));
                Dispatcher.UIThread.RunJobs();
            }
        }
        finally
        {
            window.Close();
        }
    }

    public static void FanCurveKeepsActionsVisible(string? screenshotDirectory)
    {
        var snapshot = $$"""
            {"deviceId":"fixture","model":"MSI PSU","revision":"1","observedAt":"{{DateTimeOffset.UtcNow:O}}",
             "pinCurrentsA":[],"temperatureC":48,"fanRpm":350,"automaticFan":false,"fanDutyPercent":13,"targetFanDutyPercent":25,
             "fanControllerMessage":"Fan controller test warning."}
            """;
        using var shell = CreateShell(address => new HttpClient(new JsonResponseHandler(snapshot)) { BaseAddress = address });
        var frame = DashboardFixture(shell);
        shell.Dashboard.Apply(frame, shell.Preferences, frame.Timestamp);

        var editor = new MsiSettingsEditorViewModel(new MsiOptions
        {
            FanControl = MsiFanControl.Curve,
            FanCurve = new MsiFanCurve([new(30, 0), new(40, 25), new(50, 40), new(60, 50), new(70, 75), new(80, 100)])
        });
        var window = new FanCurveWindow(editor, shell) { RequestedThemeVariant = ThemeVariant.Dark };
        var defaultWidth = window.Width;
        var defaultHeight = window.Height;
        window.Width = 800;
        window.Height = 520;
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var requested = window.GetVisualDescendants().OfType<MetricDisplay>().Single(item => item.Label == "Requested fan duty");
            TestAssert.Equal("25 %", requested.Value);
            var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().Single(viewer => viewer.Name == "CurvePointScroll");
            TestAssert.True(scroll.Extent.Height > scroll.Viewport.Height, "Only the point list should scroll at the minimum window height.");
            var done = window.GetVisualDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Done");
            var cancel = window.GetVisualDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Cancel");
            var restore = window.GetVisualDescendants().OfType<Button>()
                .Single(button => AutomationProperties.GetName(button) == "Restore defaults");
            var curve = window.GetVisualDescendants().OfType<SimpleCurveControl>().Single();
            TestAssert.True(curve.Markers.Any(marker => !marker.Filled && marker.Position == new Point(48, 25)),
                "The requested fan duty should use a separate hollow marker.");
            TestAssert.True(window.GetVisualDescendants().OfType<ContentControl>()
                .Any(item => item is { IsVisible: true, Content: "Fan controller test warning." }),
                "The backend fan controller warning should be shown.");
            var restorePosition = restore.TranslatePoint(default, window)!.Value;
            foreach (var action in new[] { restore, cancel, done })
            {
                var position = action.TranslatePoint(default, window)!.Value;
                TestAssert.Equal(restorePosition.Y, position.Y);
                TestAssert.True(position.Y + action.Bounds.Height <= window.ClientSize.Height,
                    "Actions should stay visible while the point table scrolls.");
                TestAssert.True(position.X >= 0 && position.X + action.Bounds.Width <= window.ClientSize.Width,
                    "The action row should fit at the minimum window width.");
            }

            var add = window.GetVisualDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Add point");
            TestAssert.True(!add.IsEnabled, "The editor should cap the curve at six points.");
            var pointInputs = scroll.GetVisualDescendants().OfType<NumericUpDown>().ToArray();
            TestAssert.Equal(12, pointInputs.Length);
            TestAssert.True(pointInputs.All(input => input.ShowButtonSpinner), "Curve points should use visible steppers.");
            var temperature = pointInputs.First();
            var text = temperature.GetVisualDescendants().OfType<TextBox>().Single();
            TestAssert.Equal(TextAlignment.Center, text.TextAlignment);
            TestAssert.Equal(Avalonia.Layout.VerticalAlignment.Center, text.VerticalContentAlignment);
            text.Focus();
            window.KeyTextInput("abc");
            Dispatcher.UIThread.RunJobs();
            TestAssert.Equal("30", text.Text);
            TestAssert.Equal(30d, editor.CurvePoints[0].TemperatureC);

            var duty = pointInputs[1];
            var up = duty.GetVisualDescendants().OfType<RepeatButton>().Single(button => button.Name == "PART_IncreaseButton");
            var location = up.TranslatePoint(new Point(up.Bounds.Width / 2, up.Bounds.Height / 2), window)!.Value;
            window.MouseDown(location, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(location, MouseButton.Left, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            TestAssert.Equal(25m, duty.Value!.Value);
            TestAssert.Equal(0d, editor.CurvePoints[0].DutyPercent);
            AvaloniaControlsTests.Capture(window, screenshotDirectory, "fan-curve-compact.png");

            window.Width = defaultWidth;
            window.Height = defaultHeight;
            Dispatcher.UIThread.RunJobs();

            var readouts = window.GetVisualDescendants().OfType<MetricDisplay>().ToArray();
            TestAssert.Equal(3, readouts.Length);
            var firstReadoutY = readouts[0].TranslatePoint(default, window)!.Value.Y;
            var labels = readouts.Select(readout => readout.GetVisualDescendants().OfType<TextBlock>()
                .Single(label => label.Text == readout.Label)).ToArray();
            foreach (var readout in readouts)
            {
                TestAssert.Equal(firstReadoutY, readout.TranslatePoint(default, window)!.Value.Y);
            }

            foreach (var label in labels)
            {
                var singleLine = new TextBlock { Text = label.Text, FontFamily = label.FontFamily, FontSize = label.FontSize };
                singleLine.Measure(Size.Infinity);
                TestAssert.True(label.Bounds.Height <= singleLine.DesiredSize.Height + 1, $"'{label.Text}' should fit on one line.");
            }

            AvaloniaControlsTests.Capture(window, screenshotDirectory, "fan-curve.png");
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            TestAssert.True(!window.IsVisible, "Cancel should close the curve editor.");
            TestAssert.Equal(0d, editor.CurvePoints[0].DutyPercent);
        }
        finally
        {
            if (window.IsVisible)
            {
                window.Close();
            }
        }

        var applyWindow = new FanCurveWindow(editor, shell);
        applyWindow.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var firstDuty = applyWindow.GetVisualDescendants().OfType<NumericUpDown>()
                .First(input => AutomationProperties.GetName(input) == "Duty (%)");
            firstDuty.SetCurrentValue(NumericUpDown.ValueProperty, 35m);
            var done = applyWindow.GetVisualDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Done");
            done.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            TestAssert.True(!applyWindow.IsVisible, "Done should close the curve editor after validating the draft.");
            TestAssert.Equal(35d, editor.CurvePoints[0].DutyPercent);
        }
        finally
        {
            if (applyWindow.IsVisible)
            {
                applyWindow.Close();
            }
        }
    }

    public static void FanCurveRestoreStagesAutomaticMode()
    {
        using var shell = CreateShell(address => new HttpClient(new JsonResponseHandler("{}")) { BaseAddress = address });
        var editor = new MsiSettingsEditorViewModel(new MsiOptions { FanControl = MsiFanControl.Curve });
        var window = new FanCurveWindow(editor, shell);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var restore = window.GetVisualDescendants().OfType<Button>()
                .Single(button => AutomationProperties.GetName(button) == "Restore defaults");
            restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            TestAssert.Equal((int)MsiFanControl.Automatic, editor.FanModeIndex);
            TestAssert.True(!window.IsVisible, "Restore defaults should return to the main configuration.");
        }
        finally
        {
            if (window.IsVisible)
            {
                window.Close();
            }
        }
    }

    public static void SecondInstanceQueuesActivationDuringStartup()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var first = new SingleInstance(diagnostic: true);
        using var second = new SingleInstance(diagnostic: true);
        TestAssert.True(first.IsFirst && !second.IsFirst, "Only the first process may open the main window.");

        using var activated = new ManualResetEventSlim();
        second.RequestActivation();
        first.ListenForActivation(activated.Set);
        TestAssert.True(activated.Wait(TimeSpan.FromSeconds(3)), "Activation should survive until the main window is ready.");
    }

    public static void SafeguardReportDisplaysPinCharts()
    {
        var time = DateTimeOffset.UtcNow;
        var telemetry = new SafeguardTelemetry(time,
        [
            new(new("wireview-pro2", "fixture", SensorComponent.GpuPin, SensorMetric.Current, 1), SensorUnits.For(SensorMetric.Current), 7.5, time),
            new(new("wireview-pro2", "fixture", SensorComponent.GpuPin, SensorMetric.Power, 1), SensorUnits.For(SensorMetric.Power), 90, time),
            new(new("wireview-pro2", "fixture", SensorComponent.GpuPin, SensorMetric.Voltage, 1), SensorUnits.For(SensorMetric.Voltage), 12.0, time)
        ]);
        var report = new SafeguardReport(Guid.NewGuid(), SafeguardScripts.Simulation, time, 2, 5000, "Complete", telemetry);
        var window = new SafeguardExecutionReportWindow(report);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var titles = window.GetVisualDescendants().OfType<SectionPanel>().Select(panel => panel.Header?.ToString()).ToArray();
            foreach (var metric in new[] { "Current", "Power", "Voltage" })
            {
                TestAssert.True(titles.Contains($"WireView Pro II · Pins · {metric}"), $"The report should show a separate {metric} chart.");
            }

            var pins = window.GetVisualDescendants().OfType<PinBar>().ToArray();
            TestAssert.Equal(3, pins.Length);
            TestAssert.True(pins.All(pin => pin is { Label: "P1", FillFraction: > 0 }), "The charts should show the recorded pin values.");
        }
        finally
        {
            window.Close();
        }
    }

    public static void MatchesEventTableAndDialogLayouts(string? screenshotDirectory)
    {
        var now = DateTimeOffset.Now;
        var history = new ServiceLogPage("headless", 1, 3,
        [
            new(1, now.AddSeconds(-25), ServiceLogLevel.Information, "started", "Service", "Sensor stream started.", null),
            new(2, now.AddSeconds(-12), ServiceLogLevel.Warning, "stale", "ROG Astral", "GPU pin readings are stale.", null),
            new(3, now, ServiceLogLevel.Error, "poll", "MSI PSU", "The PSU did not respond to the telemetry request.", null)
        ]);
        var json = System.Text.Json.JsonSerializer.Serialize(history, ClientJsonContext.Default.ServiceLogPage);
        using var http = new HttpClient(new JsonResponseHandler(json)) { BaseAddress = new Uri("http://localhost/") };
        using var stop = new CancellationTokenSource();
        var window = new Window
        {
            Width = 1440,
            Height = 680,
            RequestedThemeVariant = ThemeVariant.Dark,
            Content = new EventLogPage(new ServiceLogClient(http), stop.Token)
        };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var list = window.GetVisualDescendants().OfType<ListBox>().Single();
            TestAssert.Equal(3, list.ItemCount);
            TestAssert.Equal(3L, ((ServiceLogEntry)list.Items[0]!).Sequence);
            list.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            var selected = window.GetVisualDescendants().OfType<ListBoxItem>().Single(item => item.IsSelected);
            var surface = selected.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "RowSurface");
            TestAssert.Equal(Color.Parse("#1B273C"), ((ISolidColorBrush)surface.Background!).Color);
            TestAssert.Equal(4, window.GetVisualDescendants().OfType<ColumnHeader>().Count());
            AvaloniaControlsTests.Capture(window, screenshotDirectory, "event-log.png");
        }
        finally
        {
            window.Close();
            stop.Cancel();
        }

        CheckDialog(PerformancePage.CreateControlsGuide(), "chart-controls-guide.png");
        CheckDialog(new ReleaseNotesWindow(new ReleaseNotes
        {
            ReleaseTag = "v1.0.9",
            PublishedAt = now,
            ReleaseUrl = "https://github.com/Codeh4ck/RTXLens/releases/tag/v1.0.9",
            Changelog = "1. **NVIDIA diagnostics.** Report active GPU boost limits and PCIe link status.\n"
                + "2. **Desktop GPU Status.** Show power and PCIe details alongside clocks, memory usage, temperatures, and fans."
        }), "release-notes.png");

        void CheckDialog(Window dialog, string fileName)
        {
            dialog.RequestedThemeVariant = ThemeVariant.Dark;
            dialog.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                TestAssert.Equal(WindowStartupLocation.CenterScreen, dialog.WindowStartupLocation);
                TestAssert.Equal(WindowDecorations.None, dialog.WindowDecorations);
                TestAssert.True(dialog.GetVisualDescendants().OfType<TextBlock>()
                    .Any(text => text.Name == "CaptionTitle" && text.Text == dialog.Title), "The dialog should use the app's caption.");
                TestAssert.True(!dialog.CanResize && dialog.SizeToContent.HasFlag(SizeToContent.Height),
                    "The dialog should fit its content.");
                TestAssert.True(!dialog.GetVisualDescendants().OfType<ScrollViewer>().Any(),
                    "The dialog should display all content at once.");
                AvaloniaControlsTests.Capture(dialog, screenshotDirectory, fileName);
            }
            finally
            {
                dialog.Close();
            }
        }
    }

    public static void MatchesDashboardLayoutAtWideAndCompactWidths(string? screenshotDirectory)
    {
        using var shell = CreateShell();
        shell.SetPreferences(shell.Preferences with
        {
            RememberWindowSize = false,
            RememberWindowPosition = false,
            Theme = AppTheme.Dark,
            StartupTabId = "asus-astral",
            ShowAsusPsuCurrent = true,
            AsusPsuConnector = 2
        });

        var frame = DashboardFixture(shell);
        shell.Dashboard.Apply(frame, shell.Preferences, frame.Timestamp);
        shell.Dashboard.Connection = "Service connected";

        var window = new ShellWindow(shell) { Width = 1520, Height = 890, RequestedThemeVariant = ThemeVariant.Dark };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var tabs = window.GetVisualDescendants().OfType<RadioButton>().Where(button => button.GroupName == "MainTabs");
            TestAssert.True(tabs.Select(button => button.Content as string).SequenceEqual(
                ["WireView Pro II", "ROG Astral", "MSI PSU", "GPU Status", "Performance Chart", "Event Log"]),
                "Main tabs should follow the hardware and tools order.");

            var maximize = window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "MaximizeButton");
            maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            TestAssert.Equal(WindowState.Maximized, window.WindowState);
            TestAssert.Equal("Restore", AutomationProperties.GetName(maximize));
            maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            TestAssert.Equal(WindowState.Normal, window.WindowState);
            Dispatcher.UIThread.RunJobs();

            var astralCards = Sections();
            var pins = astralCards.Single(panel => Equals(panel.Header, "Pin distribution"));
            var status = astralCards.Single(panel => Equals(panel.Header, "GPU status"));
            TestAssert.Equal(0, Grid.GetColumn(pins));
            TestAssert.Equal(1, Grid.GetColumn(status));

            var vram = window.GetVisualDescendants().OfType<ReadingText>().Single(text => text.Reading?.Contains(" / ") == true);
            TestAssert.True(vram.GetVisualAncestors().OfType<Viewbox>().Any(), "VRAM usage should stay on a single line.");
            TestAssert.True(vram.Inlines!.Count > 1, "Units should have separate muted inlines.");
            AvaloniaControlsTests.Capture(window, screenshotDirectory, "dashboard-astral.png");

            Navigate("WireView Pro II");
            var fault = window.GetVisualDescendants().OfType<MetricDisplay>().Single(metric => metric.Label == "Fault Status");
            TestAssert.Equal(4, Grid.GetColumn(fault));
            TestAssert.True(fault.Classes.Contains("error"), "Active faults should use the error palette.");
            TestAssert.Equal(Colors.White, ((ISolidColorBrush)fault.Foreground!).Color);
            var icon = fault.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "MetricIcon");
            TestAssert.Equal(Color.Parse("#FF819A"), ((ISolidColorBrush)icon.Background!).Color);
            AvaloniaControlsTests.Capture(window, screenshotDirectory, "dashboard-wireview-faults.png");

            window.Width = 900;
            Dispatcher.UIThread.RunJobs();
            TestAssert.Equal(1, Grid.GetRow(fault));
            TestAssert.Equal(1, Grid.GetColumn(fault));
            TestAssert.Equal(1, Grid.GetRow(Sections().Single(panel => Equals(panel.Header, "GPU status"))));
            AvaloniaControlsTests.Capture(window, screenshotDirectory, "dashboard-wireview-compact.png");

            window.Width = 1520;
            Navigate("GPU Status");
            TestAssert.Equal(1, Grid.GetColumn(Sections().Single(panel => Equals(panel.Header, "GPU Power Status"))));
            TestAssert.Equal(2, Grid.GetColumn(Sections().Single(panel => Equals(panel.Header, "GPU PCIe Status"))));
            var scale = window.GetVisualDescendants().OfType<RangeMarkerTrack>().Single();
            TestAssert.Equal(0.75, scale.ActivePosition!.Value);
            TestAssert.Equal(1d, scale.ReferencePosition!.Value);
            AvaloniaControlsTests.Capture(window, screenshotDirectory, "dashboard-nvidia.png");
            var details = window.GetVisualDescendants().OfType<Button>()
                .Single(button => AutomationProperties.GetName(button) == "Show boost limit details");
            var flyout = (Flyout)details.Flyout!;
            flyout.ShowAt(details);
            Dispatcher.UIThread.RunJobs();
            var presenter = ((Control)flyout.Content!).GetVisualAncestors().OfType<FlyoutPresenter>().Single();
            TestAssert.Equal(Colors.Transparent, ((ISolidColorBrush)presenter.Background!).Color);
            TestAssert.Equal(new Thickness(0), presenter.Padding);
            flyout.Hide();

            Navigate("MSI PSU");
            TestAssert.Equal(12, window.GetVisualDescendants().OfType<PinTrack>().Count());
            AvaloniaControlsTests.Capture(window, screenshotDirectory, "dashboard-msi.png");

            LoadRecording(shell.Recorder, frame);
            Navigate("Performance Chart");
            var charts = window.GetVisualDescendants().OfType<XyLineChart>().ToArray();
            TestAssert.Equal(2, charts.Length);
            TestAssert.True(charts.All(chart => chart.ShowLegendValues), "Every performance chart should show legend values.");
            TestAssert.False(window.GetVisualDescendants().OfType<ToggleSwitch>()
                .Any(toggle => Equals(toggle.OffContent, "Include values in legend")), "Legend values should not require a toggle.");
            var expander = window.GetVisualDescendants().OfType<Expander>().First();
            var header = expander.GetVisualDescendants().OfType<ToggleButton>().Single(button => button.Name == "ExpanderHeader");
            var target = header.TranslatePoint(new Point(20, 15), window)!.Value;
            window.MouseMove(target, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            var background = header.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "ToggleButtonBackground");
            TestAssert.Equal(Color.Parse("#1B273C"), ((ISolidColorBrush)background.Background!).Color);
            AvaloniaControlsTests.Capture(window, screenshotDirectory, "performance-chart.png");

            ClickShellAction(window, "Settings");
            var navigation = window.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "MainNavigation");
            var appearance = Sections().Single(panel => Equals(panel.Header, "Appearance"));
            var sidebar = window.GetVisualDescendants().OfType<TabControl>().Single();
            TestAssert.Equal(navigation.TranslatePoint(default, window)!.Value.X, sidebar.TranslatePoint(default, window)!.Value.X);
            TestAssert.Equal(RightEdge(navigation, window), RightEdge(appearance, window));
            AvaloniaControlsTests.Capture(window, screenshotDirectory, "settings-aligned.png");
        }
        finally
        {
            window.Close();
        }

        SectionPanel[] Sections() => [.. window.GetVisualDescendants().OfType<SectionPanel>()];

        void Navigate(string label)
        {
            var tab = window.GetVisualDescendants().OfType<RadioButton>().Single(button => Equals(button.Content, label));
            tab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void ClickShellAction(Window window, string label)
    {
        var button = window.GetVisualDescendants().OfType<Button>().Single(item => AutomationProperties.GetName(item) == label);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static void AssertAlignedHorizontally(Control reference, Control actual, Window window)
    {
        TestAssert.Equal(reference.TranslatePoint(default, window)!.Value.X, actual.TranslatePoint(default, window)!.Value.X);
        TestAssert.Equal(RightEdge(reference, window), RightEdge(actual, window));
    }

    private static double RightEdge(Control control, Window window) =>
        control.TranslatePoint(new Point(control.Bounds.Width, 0), window)!.Value.X;

    private static void LoadRecording(PerformanceRecorder recorder, SensorSnapshot frame)
    {
        var readings = frame.Sensors.Where(reading => reading.Identity.Component == SensorComponent.GpuPin
            || reading.Identity is { Component: SensorComponent.GpuConnector, Metric: SensorMetric.Power });
        var series = readings.Where(reading => reading.Identity.PluginId is "asus-astral" or "wireview-pro2")
            .Select((reading, index) =>
            {
                var source = reading.Identity.PluginId == "asus-astral" ? "ROG Astral" : "WireView Pro II";
                var trace = PerformanceTrace.Create(reading, source, index)!;
                var samples = Enumerable.Range(0, 61).Select(second =>
                {
                    var load = Math.Clamp((second - 10) / 6d, 0, 1) * Math.Clamp((53 - second) / 6d, 0, 1);
                    var peak = trace.Unit == "A" ? 7.6 + index * 0.08 : 510 + index * 15;
                    var baseReading = trace.Unit == "A" ? 0.8 : 45;
                    var value = baseReading + load * (peak - baseReading + Math.Sin(second * 0.6 + index) * peak * 0.012);
                    return new PerformanceSample(second, value);
                }).ToArray();

                return new PerformanceSeries(trace, true, true, 0, samples.Length, samples);
            }).ToArray();

        recorder.Import(new PerformanceChartFrame("fixture", frame.Timestamp, 60, false,
            series.Sum(item => item.Count), "Recording complete.", series));
    }

    private static SensorSnapshot DashboardFixture(ShellViewModel shell)
    {
        const string astral = "asus-astral";
        const string wireView = "wireview-pro2";
        const string nvidia = "generic-nvidia";
        const string msi = "msi-psu";
        var now = DateTimeOffset.UtcNow;
        var devices = new Dictionary<string, string>
        {
            [astral] = "nvidia-pci/01/00/2b8510de/89e31043",
            [wireView] = "wireview-pro-ii",
            [nvidia] = "NVIDIA GeForce RTX 5090",
            [msi] = "MSI MPG Ai1300TS"
        };
        shell.Dashboard.SetCatalog(new PluginCatalog(1,
        [
            .. devices.Select(pair => new PluginInfo(pair.Key, pair.Value, true, PluginState.Ready, now, null,
                pair.Key == wireView ? [PluginCapability.Restart, PluginCapability.FaultClear] : [PluginCapability.Restart]))
        ]));

        var readings = new List<SensorReading>();

        foreach (var plugin in new[] { astral, wireView, nvidia })
        {
            var loaded = plugin == wireView;
            Add(plugin, SensorComponent.GpuConnector, SensorMetric.Power, loaded ? 432 : 48.4, 1);
            Add(plugin, SensorComponent.GpuConnector, SensorMetric.Current, loaded ? 36 : 4.02, 1);
            Add(plugin, SensorComponent.GpuConnector, SensorMetric.Voltage, 12.04, 1);
        }

        for (var pin = 1; pin <= 6; pin++)
        {
            Add(astral, SensorComponent.GpuPin, SensorMetric.Current, 0.65 + pin * 0.01, pin);
            Add(wireView, SensorComponent.GpuPin, SensorMetric.Current, 6, pin);
            Add(msi, SensorComponent.PsuPin, SensorMetric.Current, 0, pin, "1");
            Add(msi, SensorComponent.PsuPin, SensorMetric.Current, 0.56 + pin * 0.01, pin, "2");
        }

        Add(wireView, SensorComponent.GpuConnector, SensorMetric.PowerCapability, 600, 1);
        Add(wireView, SensorComponent.MonitorBoard, SensorMetric.FaultStatus, 0x04);
        Add(wireView, SensorComponent.MonitorBoard, SensorMetric.FaultLog, 0x14);
        Add(wireView, SensorComponent.MonitorBoard, SensorMetric.Temperature, 40, 1);
        Add(wireView, SensorComponent.MonitorBoard, SensorMetric.Temperature, 42, 2);
        Add(wireView, SensorComponent.ExternalProbe, SensorMetric.Temperature, 35, 1);
        Add(wireView, SensorComponent.ExternalProbe, SensorMetric.Temperature, 36, 2);

        Add(nvidia, SensorComponent.GpuCore, SensorMetric.Utilization, 9);
        Add(nvidia, SensorComponent.GpuCore, SensorMetric.Voltage, 0.835);
        Add(nvidia, SensorComponent.GpuCore, SensorMetric.Clock, 1305);
        Add(nvidia, SensorComponent.GpuMemory, SensorMetric.Clock, 405);
        Add(nvidia, SensorComponent.GpuMemory, SensorMetric.MemoryUsed, 4282);
        Add(nvidia, SensorComponent.GpuMemory, SensorMetric.MemoryTotal, 32607);
        Add(nvidia, SensorComponent.GpuCore, SensorMetric.Temperature, 33.7);
        Add(nvidia, SensorComponent.GpuMemory, SensorMetric.Temperature, 44);
        for (var fan = 1; fan <= 2; fan++)
        {
            Add(nvidia, SensorComponent.GpuFan, SensorMetric.FanSpeed, 0, fan);
            Add(nvidia, SensorComponent.GpuFan, SensorMetric.FanDuty, 0, fan);
        }

        Add(nvidia, SensorComponent.GpuPowerLimits, SensorMetric.MinimumPowerLimit, 400);
        Add(nvidia, SensorComponent.GpuPowerLimits, SensorMetric.MaximumPowerLimit, 600);
        Add(nvidia, SensorComponent.GpuPowerLimits, SensorMetric.EnforcedPowerLimit, 550);
        Add(nvidia, SensorComponent.GpuPowerLimits, SensorMetric.DefaultPowerLimit, 600);
        Add(nvidia, SensorComponent.GpuPerformance, SensorMetric.ActiveClockReasons, 1);
        Add(nvidia, SensorComponent.GpuPerformance, SensorMetric.PerformanceState, 5);
        Add(nvidia, SensorComponent.GpuPerformance, SensorMetric.SupportedPerformanceStates, 299);

        Add(nvidia, SensorComponent.GpuPcie, SensorMetric.LinkGeneration, 5);
        Add(nvidia, SensorComponent.GpuPcie, SensorMetric.LinkWidth, 8);
        Add(nvidia, SensorComponent.GpuPcie, SensorMetric.MaximumSystemLinkGeneration, 5);
        Add(nvidia, SensorComponent.GpuPcie, SensorMetric.MaximumLinkWidth, 16);
        Add(nvidia, SensorComponent.GpuPcie, SensorMetric.MaximumGpuLinkGeneration, 5);
        Add(nvidia, SensorComponent.GpuPcie, SensorMetric.ReceiveThroughput, 10240);
        Add(nvidia, SensorComponent.GpuPcie, SensorMetric.TransmitThroughput, 2048);
        Add(nvidia, SensorComponent.GpuPcie, SensorMetric.ReplayCount, 0);
        Add(nvidia, SensorComponent.GpuPcie, SensorMetric.ReplayRolloverCount, 0);

        Add(msi, SensorComponent.Psu, SensorMetric.Temperature, 47);
        Add(msi, SensorComponent.Psu, SensorMetric.FanSpeed, 0);
        Add(msi, SensorComponent.Psu, SensorMetric.FanDuty, 0);
        Add(msi, SensorComponent.Psu, SensorMetric.FanAutomatic, 1);

        return new SensorSnapshot(1, 1, now, [.. readings],
            [.. devices.Select(pair => new PluginHealth(pair.Key, PluginState.Ready, now, pair.Value))], [])
        {
            PinWarningLimits = [new(astral, devices[astral], 9.5), new(wireView, devices[wireView], 9.2)]
        };

        void Add(string plugin, SensorComponent component, SensorMetric metric, double value,
            int? channel = null, string? connector = null) =>
            readings.Add(new SensorReading(new SensorIdentity(plugin, devices[plugin], component, metric, channel, connector),
                SensorUnits.For(metric), value, now));
    }

    private static ShellViewModel CreateShell(Func<Uri, HttpClient>? transport = null)
    {
        var storage = Path.Combine(Path.GetTempPath(), $"rtxlens-{Guid.NewGuid():N}");
        var appearance = new AppearanceViewModel(Application.Current!, new AppearanceStore(Path.Combine(storage, "appearance.json")));
        return new ShellViewModel(new PreferencesStore(Path.Combine(storage, "preferences.json")), appearance, transport);
    }

    public static void SafeguardScriptDialogsFitTheirParameters()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var script in SafeguardScripts.All)
        {
            var window = new SafeguardScriptWindow(script, null, Path.GetTempPath(), 2);
            window.Show();

            try
            {
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();

                TestAssert.False(window.CanResize || window.CanMaximize, "The script dialog should have a fixed size.");
                TestAssert.Equal(SizeToContent.Height, window.SizeToContent);
                var controls = window.Body!.GetVisualDescendants().OfType<Control>().ToArray();

                foreach (var parameter in script.Parameters.Values)
                {
                    VerifyVisible(parameter.Label);
                }

                VerifyVisible("Cancel");
                VerifyVisible("Save parameters");
                if (script.SendsMail)
                {
                    VerifyVisible("Send test email");
                    var testEmail = controls.OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Send test email");
                    var icon = testEmail.GetVisualDescendants().OfType<Image>().Single();
                    TestAssert.True(ReferenceEquals(DesktopControls.Icon("mail-action"), icon.Source),
                        "The test email button should use the envelope icon.");
                }

                void VerifyVisible(string name)
                {
                    var control = controls.Single(item => AutomationProperties.GetName(item) == name);
                    var bounds = control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), window);
                    TestAssert.True(control.Bounds is { Width: > 0, Height: > 0 } && bounds is { } corner
                        && corner.X <= window.ClientSize.Width && corner.Y <= window.ClientSize.Height,
                        $"{name} should fit inside the script dialog.");
                    TestAssert.True(control.FindAncestorOfType<ScrollViewer>() is null, $"{name} should be visible without scrolling.");
                }
            }
            finally
            {
                window.Close();
            }
        }
    }

    public static void SafeguardLoadsAndReloadsConnectors(string? screenshotDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var shell = CreateShell();
        var installation = Path.Combine(Path.GetTempPath(), "afterburner-layout-fixture");
        var plugin = """
            [Settings]
            GPUSafeguard=1
            SoftwareAlarm=1
            MaxCurrent=9.2
            CurrentDif=1.2
            """u8.ToArray();
        var profile = """
            [Settings]
            Sources=+PSU 12VHPWR1 alarm,+PSU 12VHPWR2 alarm
            [Monitoring]
            PSU.dll=1
            [Source PSU 12VHPWR1 alarm]
            AlarmTimeout=2000
            [Source PSU 12VHPWR2 alarm]
            AlarmTimeout=3000
            """u8.ToArray();
        var baseline = new AfterburnerChangeSet(installation,
        [
            new("Plugins/Monitoring/PSU.cfg", plugin, plugin),
            new("Profiles/MSIAfterburner.cfg", profile, profile)
        ]);
        var window = new AfterburnerSettingsWindow(shell, () => []);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.PresentConfiguration(baseline);
            Dispatcher.UIThread.RunJobs();
            var badges = window.GetVisualDescendants().OfType<ContentControl>().Where(control => control.Classes.Contains("badge")).ToArray();
            window.PresentInstallationStatus(new AfterburnerInstallation(installation, "4.6.7.16935", true, true));
            VerifyBadge("Afterburner 4.6.7.16935", "success");
            VerifyBadge("PSU.dll present", "success");

            window.PresentInstallationStatus(new AfterburnerInstallation(installation, "4.6.6", true, true));
            VerifyBadge("Afterburner 4.6.6", "error");
            VerifyBadge("PSU.dll present", "success");

            window.PresentInstallationStatus(new AfterburnerInstallation(installation, "4.6.7.16935", false, true));
            VerifyBadge("Afterburner 4.6.7.16935", "success");
            VerifyBadge("PSU.dll unavailable", "error");

            window.PresentInstallationStatus(null);
            VerifyBadge("Afterburner version unavailable", "error");
            VerifyBadge("PSU.dll unavailable", "error");

            var tabs = window.GetVisualDescendants().OfType<TabControl>().Single(tab => tab.Name == "SafeguardConnectorTabs");
            TestAssert.Equal(2000, VisibleTimeout());
            var hiddenConnector = (AfterburnerConnectorEditor)((TabItem)tabs.Items[1]!).Content!;
            TestAssert.Equal(3000, hiddenConnector.Read().AlarmTimeoutMs);
            TestAssert.False(window.HasChanges, "A freshly loaded configuration should have no pending changes.");
            tabs.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            TestAssert.Equal(3000, VisibleTimeout());
            TestAssert.False(window.HasChanges, "Switching connectors should not create pending changes.");

            var timeout = hiddenConnector.GetVisualDescendants().OfType<ValidatedNumberInput>().Single();
            timeout.Text = "";
            TestAssert.True(window.HasChanges, "Clearing a saved timeout should count as an edit.");
            timeout.Value = 3500;
            TestAssert.True(window.HasChanges, "Changing a timeout should count as an edit.");
            timeout.Value = 3000;
            TestAssert.False(window.HasChanges, "Restoring the saved timeout should clear pending changes.");

            window.PresentConfiguration(baseline, tabs.SelectedIndex);
            Dispatcher.UIThread.RunJobs();
            TestAssert.Equal(3000, VisibleTimeout());

            var backups = Enumerable.Range(0, 14)
                .Select(index => new AfterburnerBackup(index.ToString(), DateTimeOffset.UtcNow.AddMinutes(-index),
                    index == 0 ? "Saved working configuration" : "Before apply", installation, []))
                .ToArray();
            window.PresentBackups(backups);
            Dispatcher.UIThread.RunJobs();
            var list = window.GetVisualDescendants().OfType<ListBox>().Single(control => control.Name == "SafeguardBackups");
            var buttons = window.GetVisualDescendants().OfType<Button>().ToArray();
            var date = buttons.Single(button => AutomationProperties.GetName(button) == "Sort by date and time");
            var name = buttons.Single(button => AutomationProperties.GetName(button) == "Sort by name");
            var backupHeader = date.FindAncestorOfType<Border>()!;
            HoverHeader(2, date);
            HoverHeader(backupHeader.Bounds.Width - 2, name);
            window.MouseMove(new Point(0, 0), RawInputModifiers.None);

            TestAssert.Equal("0", FirstBackup());
            list.SelectedItems!.Add(backups[0]);
            list.SelectedItems.Add(backups[1]);
            date.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            TestAssert.Equal("13", FirstBackup());
            name.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            TestAssert.Equal("1", FirstBackup());
            name.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            TestAssert.Equal("0", FirstBackup());
            TestAssert.Equal(2, list.SelectedItems.Count);

            var restart = buttons.Single(button => AutomationProperties.GetName(button) == "Start Afterburner");
            var apply = buttons.Single(button => AutomationProperties.GetName(button) == "Apply Afterburner settings");
            TestAssert.True(restart.TranslatePoint(default, window)!.Value.X < apply.TranslatePoint(default, window)!.Value.X,
                "Restart should be on the left of Apply.");
            TestAssert.Equal(SizeToContent.Manual, window.SizeToContent);
            TestAssert.Equal(1120d, window.Bounds.Width);

            Dispatcher.UIThread.RunJobs();
            var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            TestAssert.True(scroll.Extent.Height > scroll.Viewport.Height, "The backup table should scroll within its panel.");
            list.ScrollIntoView(list.ItemCount - 1);
            Dispatcher.UIThread.RunJobs();
            TestAssert.True(scroll.Offset.Y > 0, "The last backup should be reachable by scrolling.");
            list.ScrollIntoView(0);
            Dispatcher.UIThread.RunJobs();
            var outerScroll = (ScrollViewer)window.Body!;
            TestAssert.True(outerScroll.Extent.Height <= outerScroll.Viewport.Height + 1,
                "The default Safeguard+ window should fit the configuration and connector controls.");
            AvaloniaControlsTests.Capture(window, screenshotDirectory, "safeguard-settings.png");

            var connectorEditor = (AfterburnerConnectorEditor)((TabItem)tabs.Items[1]!).Content!;
            var alarmAction = connectorEditor.GetVisualDescendants().OfType<ComboBox>()
                .Single(selector => selector.Items.Contains("Run bundled script"));
            try
            {
                tabs.SelectedIndex = 0;
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                var shortHeight = window.ClientSize.Height;

                alarmAction.SelectedIndex = 2;
                tabs.SelectedIndex = 1;
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                TestAssert.True(window.ClientSize.Height > shortHeight, "The bundled-script controls should increase the window height.");
                TestAssert.True(outerScroll.Extent.Height <= outerScroll.Viewport.Height + 1,
                    "The selected connector's bundled-script controls should fit without scrolling.");
                TestAssert.Equal(1120d, window.Bounds.Width);
            }
            finally
            {
                alarmAction.SelectedIndex = 0;
                Dispatcher.UIThread.RunJobs();
            }


            window.Close();
            TestAssert.False(window.IsVisible, "Closing an untouched configuration should not open a discard dialog.");

            void HoverHeader(double x, Button button)
            {
                var point = backupHeader.TranslatePoint(new Point(x, backupHeader.Bounds.Height / 2), window)!.Value;
                window.MouseMove(point, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
                TestAssert.True(button.IsPointerOver, "The sortable header should respond to hover at the table edge.");
            }

            void VerifyBadge(string text, string tone)
            {
                var badge = badges.Single(control => Equals(control.Content, text));
                TestAssert.True(badge.Classes.Contains(tone), $"{text} should use the {tone} badge colors.");
            }

            string FirstBackup() => list.Items.Cast<AfterburnerBackup>().First().Id;
            int? VisibleTimeout() => tabs.GetVisualDescendants().OfType<AfterburnerConnectorEditor>().Single().Read().AlarmTimeoutMs;
        }
        finally
        {
            window.Close();
        }
    }

#if TELEMETRY_DEBUG
#if !DEBUG
    public static void ReleaseDebugConsoleRequiresShortcut()
    {
        using var shell = CreateShell();
        var window = new ShellWindow(shell);
        window.Show();

        try
        {
            ClickShellAction(window, "Configuration");
            var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(item => Equals(item.Header, "Maintenance"));
            Dispatcher.UIThread.RunJobs();

            var buttons = window.GetVisualDescendants().OfType<Button>().ToArray();
            var console = buttons.Single(button => AutomationProperties.GetName(button) == "Open Debug Console");
            var restart = buttons.Single(button => AutomationProperties.GetName(button) == "Restart backend service");
            TestAssert.False(console.IsVisible, "The console should start hidden in Release.");

            const KeyModifiers modifiers = KeyModifiers.Control | KeyModifiers.Alt;
            PressF11(modifiers);
            TestAssert.False(console.IsVisible, "The shortcut should require all three modifiers.");

            PressF11(modifiers | KeyModifiers.Shift);
            TestAssert.True(console.IsEffectivelyVisible, "The shortcut should reveal the Maintenance button.");
            TestAssert.False(shell.IsSimulation, "Revealing the button should leave live monitoring active.");
            TestAssert.Equal(0, window.OwnedWindows.Count);

            console.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var debugWindow = window.OwnedWindows.Single();
            TestAssert.Equal("RTX Lens - Debug Console", debugWindow.Title);
            debugWindow.Close();

            void PressF11(KeyModifiers keys)
            {
                restart.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F11, KeyModifiers = keys });
                Dispatcher.UIThread.RunJobs();
            }
        }
        finally
        {
            window.Close();
        }
    }
#endif

    public static void DebugConsoleScrollsAndKeepsOverrides(string? screenshotDirectory)
    {
        using var shell = CreateShell();
        var session = shell.Simulation;
        var wireView = session.Plugins.Single(plugin => plugin.Id == "wireview-pro2");
        wireView.ModeIndex = (int)RTXLens.Debugging.Models.DebugSourceMode.Simulated;
        session.Enabled = true;

        var console = new App.Views.Simulation.DebugConsoleWindow(session) { Width = 880, Height = 630 };
        console.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var list = console.GetVisualDescendants().OfType<ListBox>().Single(control => control.Name == "SimulationMetrics");
            var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            var categories = console.GetVisualDescendants().OfType<TabControl>()
                .Single(tab => tab.Name == "SimulationMetricCategories");
            TestAssert.Equal("Pin currents", ((TabItem)categories.SelectedItem!).Header as string);
            TestAssert.Equal(6, list.ItemCount);
            TestAssert.False(console.GetVisualDescendants().OfType<SectionPanel>().Any(panel => Equals(panel.Header, "Metrics and overrides")),
                "The metric table and category tabs should sit outside a shared section.");
            var firstPin = wireView.Metrics.Single(metric => metric.Definition is { Channel: 1, Metric: SensorMetric.Current, Component: SensorComponent.GpuPin });
            firstPin.ModeIndex = (int)RTXLens.Debugging.Models.DebugMetricMode.Unavailable;
            Dispatcher.UIThread.RunJobs();
            var firstRow = list.GetVisualDescendants().OfType<ListBoxItem>()
                .Single(item => item.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "P1 current"));
            TestAssert.Equal(2, firstRow.GetVisualDescendants().OfType<TextBlock>()
                .Count(text => text.Text == "Not reported" && text.Classes.Contains("unreported-reading")));
            TestAssert.True(scroll.Extent.Height > scroll.Viewport.Height, "The metric table should have a scrollable viewport.");
            TestAssert.True(list.GetVisualDescendants().OfType<ListBoxItem>().Count() < list.ItemCount,
                "The metric table should realize only visible rows.");

            for (var pass = 0; pass < 3; pass++)
            {
                scroll.Offset = new Vector(0, scroll.Extent.Height);
                Dispatcher.UIThread.RunJobs();
                TestAssert.True(scroll.Offset.Y > 0, "Scrolling should recycle the first metric rows.");

                var mode = list.GetVisualDescendants().OfType<ComboBox>()
                    .Single(control => AutomationProperties.GetName(control) == "P6 current mode");
                mode.SelectedIndex = (int)RTXLens.Debugging.Models.DebugMetricMode.Fixed;
                Dispatcher.UIThread.RunJobs();
                var value = list.GetVisualDescendants().OfType<NumericUpDown>()
                    .Single(control => AutomationProperties.GetName(control) == "P6 current override");
                value.Value = 8;
                Dispatcher.UIThread.RunJobs();
                var current = wireView.Metrics.Single(metric => metric.Definition is { Channel: 6, Metric: SensorMetric.Current, Component: SensorComponent.GpuPin });
                TestAssert.Equal(8d, current.Value);

                scroll.Offset = default;
                Dispatcher.UIThread.RunJobs();
                TestAssert.Equal(0d, scroll.Offset.Y);
            }

            SelectCategory("Pin voltages");
            TestAssert.Equal(6, list.ItemCount);
            TestAssert.True(list.GetVisualDescendants().OfType<ComboBox>()
                .Any(control => AutomationProperties.GetName(control) == "P1 voltage mode"), "Voltage rows should have their own tab.");
            var table = list.FindAncestorOfType<Border>()!;
            var pinTableHeight = table.Bounds.Height;
            SelectCategory("Faults");
            TestAssert.Equal(2, list.ItemCount);
            TestAssert.True(table.Bounds.Height < pinTableHeight, "The fault table should shrink to fit its two rows.");
            TestAssert.True(Math.Abs(scroll.Viewport.Height - scroll.Extent.Height) <= 1,
                "The fault list should fit its rows without extra space or a scrollbar.");
            SelectCategory("Pin currents");
            TestAssert.True(scroll.Extent.Height > scroll.Viewport.Height, "The pin table should still scroll in a short window.");

            var tabs = console.GetVisualDescendants().OfType<TabControl>().Single(tab => tab.Name == "SimulationPluginTabs");
            tabs.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            TestAssert.True(console.GetVisualDescendants().OfType<ListBox>()
                .Any(control => control.Name == "SimulationMetrics" && !ReferenceEquals(control, list)),
                "Switching plugins should display its own metric table.");
            tabs.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            TestAssert.Equal(8d, wireView.Metrics.Single(metric => metric.Definition is { Channel: 6, Metric: SensorMetric.Current, Component: SensorComponent.GpuPin }).Value);
            console.Width = 1020;
            console.Height = 830;
            Dispatcher.UIThread.RunJobs();
            AvaloniaControlsTests.Capture(console, screenshotDirectory, "debug-console.png");
            SelectCategory("Faults");
            AvaloniaControlsTests.Capture(console, screenshotDirectory, "debug-console-faults.png");

            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(item => Equals(item.Header, "MSI PSU"));
            Dispatcher.UIThread.RunJobs();
            var psuCategories = console.GetVisualDescendants().OfType<TabControl>()
                .Single(tab => tab.Name == "SimulationMetricCategories");
            var psuMetrics = console.GetVisualDescendants().OfType<ListBox>().Single(control => control.Name == "SimulationMetrics");
            TestAssert.True(psuCategories.Items.OfType<TabItem>().Select(item => item.Header as string)
                .SequenceEqual(["Connector 1", "Connector 2", "Connectors", "PSU"]),
                "The MSI PSU pin-current tabs should appear together before the connector totals.");
            TestAssert.Equal("Connector 1", ((TabItem)psuCategories.SelectedItem!).Header as string);
            AssertPsuConnector("1");

            psuCategories.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            AssertPsuConnector("2");

            void AssertPsuConnector(string connector)
            {
                TestAssert.Equal(6, psuMetrics.ItemCount);
                TestAssert.True(psuMetrics.Items.Cast<RTXLens.Debugging.ViewModels.DebugMetricViewModel>()
                    .All(metric => metric.Definition is { Component: SensorComponent.PsuPin, Metric: SensorMetric.Current } && metric.Definition.ConnectorId == connector),
                    $"The Connector {connector} tab should only show its own pin currents.");
            }

            void SelectCategory(string name)
            {
                categories.SelectedItem = categories.Items.OfType<TabItem>().Single(item => Equals(item.Header, name));
                Dispatcher.UIThread.RunJobs();
            }
        }
        finally
        {
            console.Close();
        }
    }

    public static void SimulationCanEditWireViewAndReturnsToLiveOnClose()
    {
        using var shell = CreateShell();
        var live = shell.Api;
        var liveEditor = shell.Configuration;
        var owner = new Window();
        owner.Show();
        try
        {
            shell.RevealDebugConsole();
            shell.OpenDebugConsole(owner);
            var console = owner.OwnedWindows.Single();
            var wire = shell.Simulation.Plugins.Single(plugin => plugin.Id == "wireview-pro2");
            wire.ModeIndex = (int)RTXLens.Debugging.Models.DebugSourceMode.Simulated;
            shell.Simulation.Enabled = true;
            Dispatcher.UIThread.RunJobs();
            TestAssert.True(shell.IsSimulation && !ReferenceEquals(shell.Api, live), "Simulation should replace the live API.");
            TestAssert.True(!ReferenceEquals(shell.Configuration, liveEditor), "Simulation should use a separate configuration draft.");

            var config = shell.Api.GetWireViewConfigurationAsync(default).GetAwaiter().GetResult();
            var editor = new WireViewSettingsEditor(config);
            var window = new Window { Content = editor.Content };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                TestAssert.Equal(config, editor.Read());
                var controls = window.GetVisualDescendants().ToArray();
                var name = controls.OfType<TextBox>().Single(input => AutomationProperties.GetName(input) == "Device name");
                name.Text = "Fixture WireView";
                var numeric = controls.OfType<ValidatedNumberInput>()
                    .Single(input => AutomationProperties.GetName(input) == "Per-pin overcurrent threshold (Amps)");
                numeric.Text = "999";
                TestAssert.Throws<ArgumentException>(() => editor.Read());
                numeric.Text = 8.7m.ToString(System.Globalization.CultureInfo.CurrentCulture);

                var saved = shell.Api.ApplyWireViewConfigurationAsync(editor.Read(), default).GetAwaiter().GetResult();
                TestAssert.Equal("Fixture WireView", saved.FriendlyName);
                TestAssert.Equal(8.7, saved.WireOcpFaultThresholdA);
            }
            finally
            {
                window.Close();
            }

            console.Close();
            TestAssert.True(!shell.IsSimulation && ReferenceEquals(shell.Api, live), "Closing the console should restore the live API.");
            TestAssert.True(ReferenceEquals(shell.Configuration, liveEditor), "Closing the console should restore the live configuration draft.");
            TestAssert.Equal(0, wire.ModeIndex);
        }
        finally
        {
            owner.Close();
        }
    }
#endif

    public static void UpdatesRealHardwareViewsFromFixtureFrames()
    {
        using var shell = CreateShell();

        const string wireView = "wireview-pro2";
        const string nvidia = "generic-nvidia";
        var now = DateTimeOffset.UtcNow;
        shell.Dashboard.SetCatalog(new PluginCatalog(1,
        [
            new PluginInfo(wireView, "WireView", true, PluginState.Ready, now, null, []),
            new PluginInfo(nvidia, "Nvidia", true, PluginState.Ready, now, null, [])
        ]));

        shell.Dashboard.Apply(Frame(1, 2), shell.Preferences, now);
        var panel = shell.Dashboard.ReportingPanels.Single(item => item.Id == wireView);
        var window = new Window { Width = 1450, Height = 900, Content = new HardwarePage(panel, shell) };
        window.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();
            var pin = window.GetVisualDescendants().OfType<PinBar>().Single(bar => bar.Label == "P1");
            TestAssert.Equal(0.2, pin.FillFraction);
            TestAssert.True(window.GetVisualDescendants().OfType<SectionPanel>().Any(section => Equals(section.Header, "GPU status")),
                "The WireView dashboard should host the shared GPU status panel.");

            shell.Dashboard.Apply(Frame(2, 4), shell.Preferences, now);
            Dispatcher.UIThread.RunJobs();
            TestAssert.Equal(0.4, pin.FillFraction);
            TestAssert.True(window.GetVisualDescendants().OfType<PinBar>().Contains(pin),
                "A new frame should update the existing pin visual.");
        }
        finally
        {
            window.Close();
        }

        return;

        SensorSnapshot Frame(long sequence, double current) => new(1, sequence, now,
            [
                new SensorReading(new SensorIdentity(wireView, "device", SensorComponent.GpuPin, SensorMetric.Current, 1),
                    SensorUnit.Amperes, current, now),
                new SensorReading(new SensorIdentity(nvidia, "Nvidia RTX 5090", SensorComponent.GpuCore, SensorMetric.Clock),
                    SensorUnit.Megahertz, 1650, now)
            ],
            [
                new PluginHealth(wireView, PluginState.Ready, now, null),
                new PluginHealth(nvidia, PluginState.Ready, now, null)
            ], []);
    }
}
