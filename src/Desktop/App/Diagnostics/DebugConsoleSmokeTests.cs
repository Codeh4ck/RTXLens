using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using RTXLens.App.Debugging.Views;
using RTXLens.App.Services;
using RTXLens.App.Views;
using RTXLens.Contracts.Enums;
using RTXLens.Debugging.Models;
using RTXLens.Presentation.Settings;

namespace RTXLens.App.Diagnostics;

internal static class DebugConsoleSmokeTests
{
    public static async Task RunAsync(MainWindow window, AppRuntime runtime, string output)
    {
        var preferences = runtime.Preferences;
        var liveDashboard = runtime.Dashboard;
        var liveConfiguration = runtime.Configuration;
        var liveRevision = liveConfiguration.RevisionText;
        window.Navigate("configuration");
        var configuration = (ConfigurationView)window.CurrentView!;
        configuration.SelectSection("maintenance");
        await WaitAsync(() => VisualTreeQuery.Descendants<Expander>(window.ThemeRoot).Any(item => item.Name == "MaintenancePanel"));
        VisualTreeQuery.Descendants<Expander>(window.ThemeRoot).Single(item => item.Name == "MaintenancePanel").IsExpanded = true;
        await Task.Delay(250);
        var button = VisualTreeQuery.Descendants<Button>(window.ThemeRoot).Single(item => item.Name == "OpenDebugConsoleButton");
        var invoke = (IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke);
        invoke.Invoke();
        await WaitAsync(() => runtime.DebugConsole is not null);
        var console = runtime.DebugConsole!;
        runtime.OpenDebugConsole();
        if (!ReferenceEquals(console, runtime.DebugConsole))
        {
            throw new InvalidOperationException("Opening the console twice must reuse its window.");
        }

        runtime.Settings.WarningNotifications = false;
        foreach (var plugin in runtime.Debug.Plugins)
        {
            plugin.ModeIndex = (int)DebugSourceMode.Simulated;
        }

        await WaitAsync(() => console.ThemeRoot.XamlRoot is not null);
        VisualTreeQuery.Descendants<ToggleSwitch>(console.ThemeRoot).Single(item => item.Name == "SimulationToggle").IsOn = true;
        await WaitAsync(() => runtime.Debug.Enabled && runtime.Dashboard.ReportingPanels.Count == 4);
        if (ReferenceEquals(liveDashboard, runtime.Dashboard))
        {
            throw new InvalidOperationException("Simulation must have separate panel and peak state.");
        }

        console.SelectPlugin("generic-nvidia");
        await Task.Delay(250);
        var group = VisualTreeQuery.Descendants<Expander>(console.ThemeRoot).Single(item => item.Header as string == "Temperatures");
        group.IsExpanded = true;
        await Task.Delay(250);
        var editor = VisualTreeQuery.Descendants<DebugMetricEditor>(console.ThemeRoot).Single(item =>
            item.Model.Definition is { Component: SensorComponent.GpuCore, Metric: SensorMetric.Temperature });
        VisualTreeQuery.Descendants<ComboBox>(editor).Single().SelectedIndex = (int)DebugMetricMode.Fixed;
        VisualTreeQuery.Descendants<NumberBox>(editor).Single().Value = 80;
        window.ShowPanel("asus-astral");
        await WaitAsync(() => runtime.Dashboard.GpuStatus.CoreTemperature.Value.StartsWith("80", StringComparison.Ordinal));
        var temperature = editor.Model;
        temperature.Value = 60;
        runtime.Settings.WarningNotifications = true;
        runtime.SetRendering(false);
        var ticks = runtime.RenderTicks;
        var notifications = runtime.DebugNotificationCount;
        temperature.Value = 95;
        await WaitAsync(() => runtime.DebugNotificationCount > notifications);
        if (runtime.RenderTicks != ticks)
        {
            throw new InvalidOperationException("Debug notifications must not restart hidden rendering.");
        }

        runtime.SetRendering(true);
        await WaitAsync(() => runtime.Dashboard.SelectedPanel!.Metrics.Any(metric => metric.IsWarning));
        runtime.Settings.WarningNotifications = false;

        var msi = runtime.Debug.Plugins[2];
        var psuPin = msi.Metrics.Single(metric => metric.Definition is { Component: SensorComponent.PsuPin, ConnectorId: "2", Channel: 1 });
        psuPin.ModeIndex = (int)DebugMetricMode.Fixed;
        psuPin.Value = 3.22;
        runtime.Settings.ShowAsusPsuCurrent = true;
        runtime.Settings.AsusPsuConnectorIndex = 1;
        await WaitAsync(() => runtime.Dashboard.SelectedPanel!.Pins[0].PsuCurrent == 3.22);
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        {
            runtime.Settings.Theme = theme;
            editor.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
            await SmokeTestRunner.CaptureElementAsync(console.ThemeRoot, Path.Combine(output, $"debug-console-{theme.ToString().ToLowerInvariant()}.png"));
            await SmokeTestRunner.CaptureElementAsync(window.ThemeRoot, Path.Combine(output, $"debug-monitor-{theme.ToString().ToLowerInvariant()}.png"));
        }

        window.Navigate("configuration");
        var pluginEditor = runtime.Configuration.Plugins.Single(plugin => plugin.IsAsus);
        pluginEditor.PollIntervalMs = 1100;
        await runtime.Configuration.ApplyAsync(runtime.StopToken);
        if (runtime.Configuration.Title != "Local simulation configuration" || !runtime.Configuration.Message.Contains("local simulation", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Simulated configuration must explicitly identify its local scope.");
        }

        if (liveConfiguration.RevisionText != liveRevision || liveConfiguration.IsDirty)
        {
            throw new InvalidOperationException("Debug configuration writes must not change the live configurator.");
        }

        console.Close();
        await WaitAsync(() => runtime.DebugConsole is null);
        if (runtime.Debug.Enabled)
        {
            throw new InvalidOperationException("Closing the console must stop simulation immediately.");
        }

        runtime.OpenDebugConsole();
        if (runtime.Debug.Enabled)
        {
            throw new InvalidOperationException("Reopening the console must not reactivate simulation.");
        }
        await WaitAsync(() => ReferenceEquals(runtime.Dashboard, liveDashboard) && runtime.Dashboard.ReportingPanels.Count == 2);
        if (!ReferenceEquals(runtime.Configuration, liveConfiguration))
        {
            throw new InvalidOperationException("Disabling simulation must restore the live configuration editor.");
        }

        window.ShowPanel("asus-astral");
        if (runtime.Dashboard.SelectedPanel!.DeviceText != "demo-gpu0" || runtime.Dashboard.SelectedPanel.Metrics.Any(metric => metric.IsWarning))
        {
            throw new InvalidOperationException("Disabling simulation must restore actual telemetry and discard simulated warnings.");
        }

        runtime.DebugConsole!.Close();
        foreach (var plugin in runtime.Debug.Plugins)
        {
            runtime.Debug.ResetPlugin(plugin.Id);
        }

        runtime.Settings.WarningNotifications = preferences.WarningNotifications;
        runtime.Settings.ShowAsusPsuCurrent = preferences.ShowAsusPsuCurrent;
        runtime.Settings.AsusPsuConnectorIndex = preferences.AsusPsuConnector - 1;
        runtime.Settings.Theme = AppTheme.Light;
        await File.WriteAllTextAsync(Path.Combine(output, "debug-console-results.txt"),
            "PASS: Maintenance button opens a reusable independent window; simulated plugin discovery, native metric editors, warning notifications while hidden, PSU comparison, local configuration, both themes, close/reopen, and restoration of the live dashboard/configuration.\n");
    }

    private static async Task WaitAsync(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!ready())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Debug console UI did not reach the expected state.");
            }

            await Task.Delay(50);
        }
    }
}
