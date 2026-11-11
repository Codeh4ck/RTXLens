using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using RTXLens.App.Controls;
using RTXLens.App.Views;
using RTXLens.App.Services;
using RTXLens.App.Platform.Windowing;
using RTXLens.Presentation.Settings;
using RTXLens.Presentation.Telemetry;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace RTXLens.App.Diagnostics;

internal static class SmokeTestRunner
{
    public static async Task RunAsync(MainWindow window, AppRuntime runtime, string output)
    {
        Directory.CreateDirectory(output);
        var oldError = Path.Combine(output, "smoke-error.txt");
        if (File.Exists(oldError))
        {
            File.Delete(oldError);
        }

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (runtime.LatestSequence == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        if (runtime.LatestSequence == 0)
        {
            throw new InvalidOperationException("No demo telemetry received.");
        }
        if (Environment.GetCommandLineArgs().Contains("--release-notes-and-wireview-only"))
        {
            await WaitUntilAsync(() => runtime.Dashboard.ReportingPanels.Any(panel => panel.IsWireView));
            await ReleaseNotesAndWireViewSmokeTests.RunAsync(window, runtime, output);
            return;
        }

        if (Environment.GetCommandLineArgs().Contains("--afterburner-only"))
        {
            await AfterburnerSmokeTests.RunAsync(window, runtime, output);
            return;
        }

        if (Environment.GetCommandLineArgs().Contains("--exit-confirmation-only"))
        {
            await ExitConfirmationSmokeTests.RunAsync(window, runtime, output);
            return;
        }

        if (Environment.GetCommandLineArgs().Contains("--msi-fan-curve-only"))
        {
            await MsiFanCurveSmokeTests.RunAsync(runtime, output);
            return;
        }

        if (Environment.GetCommandLineArgs().Contains("--performance-chart-only"))
        {
            await PerformanceChartSmokeTests.RunAsync(window, runtime, output);
            return;
        }

        if (Environment.GetCommandLineArgs().Contains("--gpu-status-only"))
        {
            await WaitUntilAsync(() => !runtime.Dashboard.GpuStatus.IsUnavailable && runtime.Dashboard.SelectedPanel is not null);
            await GenericNvidiaSmokeTests.RunAsync(window, runtime, output);
            return;
        }

        await WaitUntilAsync(() => runtime.Dashboard.ReportingPanels.Count == 3 && runtime.Dashboard.SelectedPanel is not null);
        if (Environment.GetCommandLineArgs().Contains("--warning-policy-only"))
        {
            await WarningPolicySmokeTests.RunAsync(window, runtime, output);
            return;
        }
        if (Environment.GetCommandLineArgs().Contains("--wireview-ui-only"))
        {
            await WireViewPresentationSmokeTests.RunAsync(window, runtime, output);
            return;
        }
        if (Environment.GetCommandLineArgs().Contains("--configuration-help-only"))
        {
            await ConfigurationHelpSmokeTests.RunAsync(window, runtime, output);
            return;
        }
        if (Environment.GetCommandLineArgs().Contains("--debug-console-only"))
        {
            await DebugConsoleSmokeTests.RunAsync(window, runtime, output);
            return;
        }
        if (window.AppWindow.Presenter is not OverlappedPresenter { IsMaximizable: false })
        {
            throw new InvalidOperationException("The compact window must not allow maximization.");
        }

        var maximum = WindowSizeLimits.GetMaximumPixels(WinRT.Interop.WindowNative.GetWindowHandle(window));
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(maximum.Width + 300, maximum.Height + 300));
        await WaitUntilAsync(() => window.AppWindow.Size.Width <= maximum.Width && window.AppWindow.Size.Height <= maximum.Height);

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var initialWorkingSet = process.WorkingSet64;
        var initialPrivateBytes = process.PrivateMemorySize64;
        await Task.Delay(200);
        var activeTab = VisualTreeQuery.Descendants<ToggleButton>(window.ThemeRoot).SingleOrDefault(button => button.Tag as string == runtime.Dashboard.SelectedPanel?.Id);
        if (activeTab?.IsChecked != true || !VisualTreeQuery.Descendants<Border>(activeTab).Any(border => border is { Name: "SelectionRail", Visibility: Visibility.Visible }))
        {
            throw new InvalidOperationException("The initial hardware tab has no visible selection highlight.");
        }

        runtime.Settings.ShowPeakHold = false;
        runtime.SetPinMetric(PinMetric.Current);
        await runtime.ReadWireViewDeviceAsync();
        runtime.Settings.Theme = AppTheme.Dark;
        window.ShowPanel("wireview-pro2");
        await CaptureAsync(window, Path.Combine(output, "wireview-dark.png"));
        window.ShowPanel("asus-astral");
        await CaptureAsync(window, Path.Combine(output, "astral-dark.png"));
        var gpuStatus = VisualTreeQuery.Descendants<GpuStatusView>(window.ThemeRoot).Single();
        var gpuLabels = VisualTreeQuery.Descendants<TextBlock>(gpuStatus).ToArray();
        var vramLabel = Array.FindIndex(gpuLabels, label => label.Text == "VRAM usage");
        var temperatureLabel = Array.FindIndex(gpuLabels, label => label.Text == "Temperatures");
        if (vramLabel < 0 || temperatureLabel <= vramLabel || gpuLabels[vramLabel].FontSize < 15)
        {
            throw new InvalidOperationException("The readable VRAM heading must appear above temperatures.");
        }

        var bars = VisualTreeQuery.Descendants<PinBar>(window.ThemeRoot).ToArray();
        if (bars.Length != 6 || bars.SelectMany(bar => VisualTreeQuery.Descendants<StatusBadge>(bar))
            .Any(badge => badge.ShowIndicator))
        {
            throw new InvalidOperationException("Headroom labels must not have dot indicators.");
        }

        var hardwareTabs = VisualTreeQuery.Descendants<ToggleButton>(window.ThemeRoot).Where(button => button.Tag is string).ToArray();
        if (hardwareTabs.Count(button => button.IsChecked == true) != 1
            || hardwareTabs.Sum(button => VisualTreeQuery.Descendants<Border>(button).Count(border => border is { Name: "SelectionRail", Visibility: Visibility.Visible })) != 1)
        {
            throw new InvalidOperationException("Switching hardware tabs must leave exactly one visible selection highlight.");
        }

        if (hardwareTabs.SelectMany(button => VisualTreeQuery.Descendants<Border>(button)).Any(border => border.Name == "TabFrame" && border.BorderThickness != new Thickness(0)))
        {
            throw new InvalidOperationException("Selected hardware tabs must use only an underline.");
        }

        runtime.SetPinMetric(PinMetric.Power);
        await CaptureAsync(window, Path.Combine(output, "astral-power-dark.png"));
        runtime.SetPinMetric(PinMetric.Voltage);
        await CaptureAsync(window, Path.Combine(output, "astral-voltage-dark.png"));
        runtime.SetPinMetric(PinMetric.Current);
        if (((HardwareView)window.CurrentView!).IsResetPeakVisible)
        {
            throw new InvalidOperationException("Reset peaks must be hidden when peak hold is disabled.");
        }

        runtime.Settings.ShowPeakHold = true;
        await Task.Delay(400);
        if (!((HardwareView)window.CurrentView!).IsResetPeakVisible)
        {
            throw new InvalidOperationException("Reset peaks must appear when peak hold is enabled.");
        }

        await CaptureAsync(window, Path.Combine(output, "astral-peaks-dark.png"));
        runtime.Settings.Theme = AppTheme.Light;
        await CaptureAsync(window, Path.Combine(output, "astral-light.png"));
        await VerifyConfigurationTabsAsync(window, runtime, output);
        await VerifyMsiAsync(window, runtime, output);
        await DebugConsoleSmokeTests.RunAsync(window, runtime, output);
        if (WidgetIcon.LoadFailures.Count > 0)
        {
            throw new InvalidOperationException("Widget SVG failed to load: " + string.Join(", ", WidgetIcon.LoadFailures.Keys));
        }

        window.Navigate("settings");
        await CaptureAsync(window, Path.Combine(output, "settings-light.png"));
        runtime.Settings.Theme = AppTheme.Dark;
        window.ShowPanel("asus-astral");
        runtime.SetDemoDeviceAvailable("asus-astral", false);
        await WaitUntilAsync(() => runtime.Dashboard.ReportingPanels.Count == 1 && runtime.Dashboard.HasPluginWarning);
        if (runtime.Dashboard.SelectedPanel?.Id != "wireview-pro2")
        {
            throw new InvalidOperationException("A disappearing selected tab must select another reporting plugin.");
        }

        await CaptureAsync(window, Path.Combine(output, "missing-device-dark.png"));
        runtime.SetDemoDeviceAvailable("wireview-pro2", false);
        await WaitUntilAsync(() => runtime.Dashboard.ReportingPanels.Count == 0);
        await CaptureAsync(window, Path.Combine(output, "no-devices-dark.png"));
        window.Navigate("configuration");
        runtime.Configuration.Plugins.Single(plugin => plugin.Id == "asus-astral").Enabled = false;
        await runtime.Configuration.ApplyAsync(runtime.StopToken);
        runtime.SetDemoDeviceAvailable("wireview-pro2", true);
        await WaitUntilAsync(() => runtime.Dashboard.ReportingPanels.Count == 1 && !runtime.Dashboard.HasPluginWarning);
        if (window.CurrentView is not ConfigurationView)
        {
            throw new InvalidOperationException("Reporting changes must not interrupt configuration navigation.");
        }

        runtime.Configuration.Plugins.Single(plugin => plugin.Id == "asus-astral").Enabled = true;
        await runtime.Configuration.ApplyAsync(runtime.StopToken);
        runtime.SetDemoDeviceAvailable("asus-astral", true);
        await WaitUntilAsync(() => runtime.Dashboard.ReportingPanels.Count == 2);
        runtime.Configuration.Plugins.Single(plugin => plugin.Id == "wireview-pro2").Enabled = false;
        await runtime.Configuration.ApplyAsync(runtime.StopToken);
        await WaitUntilAsync(() => runtime.Dashboard.ReportingPanels.Count == 1 && !runtime.Dashboard.HasPluginWarning);
        window.ShowPanel("asus-astral");
        await CaptureAsync(window, Path.Combine(output, "astral-only-dark.png"));
        if (!VisualTreeQuery.Descendants<Border>(window.ThemeRoot).Any(border => border is { Name: "NavigationStrip", Visibility: Visibility.Visible, Background: not null }))
        {
            throw new InvalidOperationException("The navigation strip must remain visible with a single plugin tab.");
        }

        await VerifyDisabledWireViewTabsAsync(window, runtime, output);
        runtime.Configuration.Plugins.Single(plugin => plugin.Id == "wireview-pro2").Enabled = true;
        await runtime.Configuration.ApplyAsync(runtime.StopToken);
        await WaitUntilAsync(() => runtime.Dashboard.ReportingPanels.Count == 2);
        window.ShowPanel("wireview-pro2");
        runtime.SetRendering(false);
        window.AppWindow.Hide();
        if (window.CurrentView is not null)
        {
            throw new InvalidOperationException("Hidden panels were not unloaded.");
        }

        var sequence = runtime.LatestSequence;
        var ticks = runtime.RenderTicks;
        // Diagnostic capture creates large pixel buffers; release those before measuring the idle app.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        process.Refresh();
        var cpu = process.TotalProcessorTime;
        await Task.Delay(4000);
        process.Refresh();
        if (runtime.LatestSequence <= sequence)
        {
            throw new InvalidOperationException("Hidden monitoring stopped.");
        }

        if (runtime.RenderTicks != ticks)
        {
            throw new InvalidOperationException("Rendering continued while hidden.");
        }

        var hiddenCpu = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        var report = $"PASS: WinUI pages, dark/light themes and native rendering.\n"
            + "PASS: Initial tab highlight, configuration footer/revert, peak-reset visibility and SVG loading.\n"
            + "PASS: Underline-only tabs, headroom without dots, two-decimal threshold formatting and reporting-only navigation with missing/disabled/recovered devices.\n"
            + "PASS: Compact maximum window size, disabled maximization, visible single-plugin tab strip, larger outlined metric rows and VRAM ordering.\n"
            + "PASS: Configuration tabs preserve drafts and selection across reloads; source selections are paneled; maintenance matches panel colors in both themes.\n"
            + "PASS: Disabled WireView settings survive 160 tab selections and 20 overlapping configuration reloads across four window sizes.\n"
            + $"PASS: Hidden panels unloaded; telemetry advanced from {sequence} to {runtime.LatestSequence}; render ticks unchanged.\n"
            + $"Initial working set before captures: {initialWorkingSet / 1048576d:F1} MiB\nInitial private bytes: {initialPrivateBytes / 1048576d:F1} MiB\n"
            + $"Hidden working set after diagnostic capture cleanup: {process.WorkingSet64 / 1048576d:F1} MiB\nHidden private bytes: {process.PrivateMemorySize64 / 1048576d:F1} MiB\n"
            + $"CPU time during 4 seconds hidden: {hiddenCpu:F1} ms\nTray icon available: {runtime.TrayAvailable}\n"
            + $"OS: {Environment.OSVersion}\nRuntime: {Environment.Version}\n";
        report += $"Window pixels: {window.AppWindow.Size.Width} x {window.AppWindow.Size.Height}\n";
        report += "PASS: MSI configuration draft/apply, twelve current-only bars in both themes, and missing-device recovery.\n";
        report += "PASS: Independent WireView/Astral PSU connector settings, comparison values below all six pins in both themes, and source-loss clearing/recovery.\n";
        await File.WriteAllTextAsync(Path.Combine(output, "smoke-results.txt"), report);
    }

    private static async Task VerifyDisabledWireViewTabsAsync(MainWindow window, AppRuntime runtime, string output)
    {
        window.Navigate("configuration");
        var view = (ConfigurationView)window.CurrentView!;
        if (runtime.Configuration.Plugins.Single(plugin => plugin.Id == "wireview-pro2").Enabled)
        {
            throw new InvalidOperationException("The disabled-WireView regression requires WireView to be disabled.");
        }

        var originalSize = window.AppWindow.Size;
        var originalTheme = runtime.Settings.Theme;
        var originalTextScale = runtime.Settings.TextScale;
        var scale = window.ThemeRoot.XamlRoot.RasterizationScale;
        foreach (var size in new[] { (Width: 1150, Height: 927), (Width: 1100, Height: 800), (Width: 900, Height: 700), (Width: 720, Height: 550) })
        {
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(size.Width * scale), (int)(size.Height * scale)));
            await Task.Delay(100);
            view = (ConfigurationView)window.CurrentView!;
            var tabs = VisualTreeQuery.Descendants<TabView>(view).Single();
            if (tabs.VerticalAlignment != VerticalAlignment.Stretch)
            {
                throw new InvalidOperationException("Configuration tabs must use a bounded, stretched content area.");
            }

            var wireTab = tabs.TabItems.OfType<TabViewItem>().Single(tab => tab.Tag as string == "plugin:wireview-pro2");
            for (var iteration = 0; iteration < 20; iteration++)
            {
                await File.WriteAllTextAsync(Path.Combine(output, "tab-stress-progress.txt"), $"{size.Width}x{size.Height}, iteration {iteration}, selecting Astral then disabled WireView");
                if (iteration % 10 == 0)
                {
                    runtime.Settings.Theme = iteration == 0 ? AppTheme.Dark : AppTheme.Light;
                    runtime.Settings.TextScale = iteration == 0 ? 1 : 1.5;
                }
                var refresh = iteration % 4 == 0 ? ReloadAfterDelayAsync(runtime) : Task.CompletedTask;
                view.SelectSection("plugin:asus-astral");
                await Task.Delay(40);
                view.SelectSection("plugin:wireview-pro2");
                await Task.Delay(40);
                await refresh;
                if (!ReferenceEquals(wireTab, tabs.SelectedItem))
                {
                    throw new InvalidOperationException("Configuration refresh must keep the selected plugin's tab container stable.");
                }

                if (view.SelectedSection != "plugin:wireview-pro2" || runtime.Configuration.IsDirty)
                {
                    throw new InvalidOperationException("Disabled WireView tab selection must survive metadata refresh without changing settings.");
                }
            }
        }
        runtime.Settings.Theme = originalTheme;
        runtime.Settings.TextScale = originalTextScale;
        window.AppWindow.Resize(originalSize);
        await Task.Delay(100);
        window.Navigate("configuration");
        view = (ConfigurationView)window.CurrentView!;
        view.SelectSection("plugin:wireview-pro2");
        await CaptureAsync(window, Path.Combine(output, "wireview-disabled-settings.png"));
    }

    private static async Task VerifyMsiAsync(MainWindow window, AppRuntime runtime, string output)
    {
        window.Navigate("configuration");
        var view = (ConfigurationView)window.CurrentView!;
        view.SelectSection("plugin:msi-psu");
        await runtime.RefreshMsiDevicesAsync();
        await Task.Delay(200);
        if (runtime.Configuration.IsDirty)
        {
            throw new InvalidOperationException("Opening MSI settings must not create a draft.");
        }

        var plugin = runtime.Configuration.Plugins.Single(item => item.IsMsi);
        plugin.Enabled = true;
        plugin.Msi.FanModeIndex = 2;
        plugin.Msi.FanDutyPercent = 75;
        await CaptureAsync(window, Path.Combine(output, "msi-configuration-light.png"));
        if (!view.IsFooterVisible)
        {
            throw new InvalidOperationException("MSI fan changes must show the edit footer.");
        }

        await runtime.Configuration.ApplyAsync(runtime.StopToken);
        await WaitUntilAsync(() => runtime.Dashboard.ReportingPanels.Count == 3);
        runtime.SetPinMetric(PinMetric.Voltage);
        window.ShowPanel("msi-psu");
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            runtime.Settings.Theme = theme;
            await CaptureAsync(window, Path.Combine(output, $"msi-{theme.ToString().ToLowerInvariant()}.png"));
            var bars = VisualTreeQuery.Descendants<PinBar>(window.ThemeRoot).ToArray();
            if (window.CurrentView is not MsiHardwareView || bars.Length != 12 || bars.Any(bar => bar.Model.Unit != "A"))
            {
                throw new InvalidOperationException("MSI must render twelve distinct current-only pin bars.");
            }
        }
        await VerifyPsuComparisonsAsync(window, runtime, output);
        runtime.SetDemoDeviceAvailable("msi-psu", false);
        await WaitUntilAsync(() => runtime.Dashboard.ReportingPanels.Count == 2 && runtime.Dashboard.HasPluginWarning);
        if (runtime.Dashboard.Panels.Any(panel => panel.ShowPsuComparison))
        {
            throw new InvalidOperationException("PSU comparison must disappear when MSI stops reporting.");
        }
        runtime.SetDemoDeviceAvailable("msi-psu", true);
        await WaitUntilAsync(() => runtime.Dashboard.ReportingPanels.Count == 3);
        await WaitUntilAsync(() => runtime.Dashboard.Panels.Where(panel => panel.IsAsus || panel.IsWireView).All(panel => panel.ShowPsuComparison));
        runtime.Settings.ShowWireViewPsuCurrent = false;
        runtime.Settings.ShowAsusPsuCurrent = false;
        window.Navigate("configuration");
        plugin = runtime.Configuration.Plugins.Single(item => item.IsMsi);
        plugin.Enabled = false;
        plugin.Msi.FanModeIndex = 0;
        plugin.Msi.FanDutyPercent = 50;
        await runtime.Configuration.ApplyAsync(runtime.StopToken);
        await WaitUntilAsync(() => runtime.Dashboard.ReportingPanels.Count == 2);
        runtime.SetPinMetric(PinMetric.Current);
        runtime.Settings.Theme = AppTheme.Light;
    }

    private static async Task VerifyPsuComparisonsAsync(MainWindow window, AppRuntime runtime, string output)
    {
        window.Navigate("configuration");
        ((ConfigurationView)window.CurrentView!).SelectSection("plugin:msi-psu");
        await WaitUntilAsync(() => VisualTreeQuery.Descendants<ToggleSwitch>(window.ThemeRoot)
            .Any(control => control.Header as string == "Show MSI current under WireView pins"));
        var switches = VisualTreeQuery.Descendants<ToggleSwitch>(window.ThemeRoot).ToArray();
        switches.Single(control => control.Header as string == "Show MSI current under WireView pins").IsOn = true;
        switches.Single(control => control.Header as string == "Show MSI current under ROG Astral pins").IsOn = true;
        var connectors = VisualTreeQuery.Descendants<ComboBox>(window.ThemeRoot).ToArray();
        connectors.Single(control => control.Header as string == "WireView · PSU connector").SelectedIndex = 0;
        connectors.Single(control => control.Header as string == "ROG Astral · PSU connector").SelectedIndex = 1;
        await WaitUntilAsync(() => runtime.Settings.Value is { ShowWireViewPsuCurrent: true, ShowAsusPsuCurrent: true, WireViewPsuConnector: 1, AsusPsuConnector: 2 });
        await CaptureAsync(window, Path.Combine(output, "psu-comparison-settings.png"));

        foreach (var plugin in new[] { "wireview-pro2", "asus-astral" })
        {
            window.ShowPanel(plugin);
            await WaitUntilAsync(() => runtime.Dashboard.SelectedPanel?.ShowPsuComparison == true);
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                runtime.Settings.Theme = theme;
                await CaptureAsync(window, Path.Combine(output, $"{plugin}-psu-{theme.ToString().ToLowerInvariant()}.png"));
                var bars = VisualTreeQuery.Descendants<PinBar>(window.ThemeRoot).ToArray();
                var connector = plugin == "wireview-pro2" ? 0 : 1;
                var sourcePins = runtime.Dashboard.Panels.Single(panel => panel.IsMsi).Msi.Connectors[connector].Pins;
                if (bars.Length != 6 || bars.Any(bar => !bar.IsPsuCurrentVisible || bar.Model.PsuCurrent != sourcePins[bar.Model.Channel - 1].Current)
                    || bars.Any(bar => bar.Model.Unit != "V"))
                {
                    throw new InvalidOperationException("Each GPU pin must show its selected PSU connector current below the label, independently of chart units.");
                }
            }
        }
    }

    private static async Task ReloadAfterDelayAsync(AppRuntime runtime)
    {
        await Task.Delay(10);
        await runtime.Configuration.ReloadAsync(runtime.StopToken);
    }

    private static async Task VerifyConfigurationTabsAsync(MainWindow window, AppRuntime runtime, string output)
    {
        window.Navigate("configuration");
        var view = (ConfigurationView)window.CurrentView!;
        await runtime.RefreshDevicesAsync();
        await CaptureAsync(window, Path.Combine(output, "configuration-light.png"));
        var tabs = VisualTreeQuery.Descendants<TabView>(window.ThemeRoot).Single();
        if (tabs.TabItems.Count != runtime.Configuration.Plugins.Count + 4 || tabs.TabItems.OfType<TabViewItem>().Any(tab => tab.IsClosable))
        {
            throw new InvalidOperationException("Every installed plugin, stream, sources and maintenance must have a permanent configuration tab.");
        }

        view.SelectSection("plugin:asus-astral");
        await runtime.Configuration.ReloadAsync(runtime.StopToken);
        if (view.SelectedSection != "plugin:asus-astral")
        {
            throw new InvalidOperationException("Reload must preserve the selected plugin tab.");
        }

        await Task.Delay(500);
        await CaptureAsync(window, Path.Combine(output, "configuration-asus-light.png"));
        var asusSettings = VisualTreeQuery.Descendants<PluginSettingsView>(window.ThemeRoot).Single(item => item.Model.IsAsus);
        var commonInputs = VisualTreeQuery.Descendants<NumberBox>(asusSettings).Where(box => box.Header as string is "Polling interval (ms)" or "Stale after (ms)").ToArray();
        if (commonInputs.Length != 2 || commonInputs.Any(box => box.ActualHeight == 0 || box.Visibility != Visibility.Visible)
            || VisualTreeQuery.Descendants<ToggleSwitch>(asusSettings).Single().ActualHeight == 0)
        {
            throw new InvalidOperationException("Each plugin tab must retain enablement, polling and freshness controls.");
        }

        var threshold = VisualTreeQuery.Descendants<NumberBox>(asusSettings).Single(box => box.Name == "OvercurrentInput");
        if (threshold.Text != threshold.Value.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture))
        {
            throw new InvalidOperationException("Current thresholds must display at most two decimal places: " + threshold.Text);
        }

        if (runtime.Configuration.IsDirty || view.IsFooterVisible)
        {
            throw new InvalidOperationException("Opening configuration tabs must not modify settings or show the edit footer.");
        }

        view.SelectSection("stream");
        var interval = runtime.Configuration.StreamIntervalMs;
        runtime.Configuration.StreamIntervalMs = interval + 100;
        await CaptureAsync(window, Path.Combine(output, "configuration-edited-light.png"));
        if (!view.IsFooterVisible)
        {
            throw new InvalidOperationException("The configuration footer must appear for changed settings.");
        }

        view.SelectSection("sources");
        runtime.Configuration.AddSelection();
        await CaptureAsync(window, Path.Combine(output, "configuration-source-edited-light.png"));
        var selection = runtime.Configuration.Selections.Single();
        view.SelectSection("plugin:asus-astral");
        view.SelectSection("sources");
        if (runtime.Configuration.StreamIntervalMs != interval + 100 || !ReferenceEquals(selection, runtime.Configuration.Selections.Single()) || view.SelectedSection != "sources")
        {
            throw new InvalidOperationException("Tab switches and source-list changes must preserve the entire configuration draft.");
        }

        runtime.Configuration.RemoveSelection(selection);
        runtime.Configuration.StreamIntervalMs = interval;
        await Task.Delay(100);
        if (runtime.Configuration.IsDirty || view.IsFooterVisible)
        {
            throw new InvalidOperationException("The configuration footer must hide after reverting edits across tabs.");
        }

        await runtime.Configuration.ReloadAsync(runtime.StopToken);
        if (view.SelectedSection != "sources")
        {
            throw new InvalidOperationException("Reload must preserve the selected configuration section.");
        }

        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            runtime.Settings.Theme = theme;
            view.SelectSection("sources");
            await CaptureAsync(window, Path.Combine(output, $"configuration-sources-{theme.ToString().ToLowerInvariant()}.png"));
            var sourcePanel = VisualTreeQuery.Descendants<SectionPanel>(window.ThemeRoot).Single(panel => panel.Name == "SourcesPanel");
            var sources = VisualTreeQuery.Descendants<Border>(sourcePanel).First();
            var expectedColor = ((SolidColorBrush)sources.Background).Color;
            view.SelectSection("maintenance");
            await CaptureAsync(window, Path.Combine(output, $"configuration-maintenance-collapsed-{theme.ToString().ToLowerInvariant()}.png"));
            var maintenance = VisualTreeQuery.Descendants<Expander>(window.ThemeRoot).Single(item => item.Name == "MaintenancePanel");
            var header = VisualTreeQuery.Descendants<ToggleButton>(maintenance).Single(item => item.Name == "ExpanderHeader");
            if (((SolidColorBrush)header.Background).Color != expectedColor || ((SolidColorBrush)maintenance.Background).Color != expectedColor)
            {
                throw new InvalidOperationException("Maintenance header and content must match the other configuration panels in " + theme);
            }

            maintenance.IsExpanded = true;
            await CaptureAsync(window, Path.Combine(output, $"configuration-maintenance-{theme.ToString().ToLowerInvariant()}.png"));
            maintenance.IsExpanded = false;
        }
        runtime.Settings.Theme = AppTheme.Light;
    }

    private static Task CaptureAsync(MainWindow window, string path) => CaptureElementAsync(window.ThemeRoot, path);

    internal static async Task CaptureElementAsync(FrameworkElement root, string path)
    {
        await Task.Delay(350);
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(root);
        var buffer = await bitmap.GetPixelsAsync();
        using var reader = DataReader.FromBuffer(buffer);
        var pixels = new byte[buffer.Length];
        reader.ReadBytes(pixels);
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
        await encoder.FlushAsync();
        stream.Seek(0);
        using var encoded = new DataReader(stream.GetInputStreamAt(0));
        await encoded.LoadAsync((uint)stream.Size);
        var png = new byte[stream.Size];
        encoded.ReadBytes(png);
        await File.WriteAllBytesAsync(path, png);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        if (!condition())
        {
            throw new TimeoutException("The expected demo UI transition did not occur.");
        }
    }
}
