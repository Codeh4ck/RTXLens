using RTXLens.Plugins.Asus.Contracts.Warnings;
using System.Text.Json;
using RTXLens.Client.Serialization;
using RTXLens.Contracts.Alerts;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Discovery;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Health;
using RTXLens.Contracts.Monitoring;
using RTXLens.Contracts.Sensors;
using RTXLens.Debugging;
using RTXLens.Debugging.Api;
using RTXLens.Debugging.Models;
using RTXLens.Debugging.ViewModels;
using RTXLens.Plugins.Msi.Contracts.Configuration;
using RTXLens.Plugins.WireView.Contracts.Input;
using RTXLens.Presentation.Notifications;
using RTXLens.Presentation.Settings;
using RTXLens.Presentation.Telemetry;
using RTXLens.Tests.Infrastructure;

namespace RTXLens.UI.Tests.Debugging;

internal static class DebugSessionTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        runner.Run(StartsDisabledAndBuildsAllPluginsWithoutAService);
        runner.Run(EditsEveryMetricWithoutLosingItsIdentity);
        runner.Run(OverridesOnlySelectedMetricsAndPreservesTheLiveFrame);
        runner.Run(DoesNotRefreshLiveTimestampsOnSimulationTicks);
        runner.Run(SimulatesPluginInstallationEnablementAndRecovery);
        runner.Run(SupportsMissingStaleAndResetMetrics);
        runner.Run(DerivesTotalsAndAllowsIndependentOverrides);
        runner.Run(UsesProductionAlertDurationsAndPerPinRecovery);
        runner.Run(OtherPluginEditsDoNotResetAsusTimers);
        runner.Run(TriggersGroupedNotificationsAndDeduplicatesServerEvents);
        runner.Run(RejectsInvalidValuesAndKeepsFiniteReadings);
        runner.Run(ResetAllDiscardsLocalConfigurationAndOverrides);
        runner.Run(ResolvesSelectionsAgainstTheEffectiveReadings);
        await runner.RunAsync(ConfigurationAndDeviceCommandsRemainLocalAsync);
        await runner.RunAsync(WireViewSettingsAreEditableOnlyForTheCurrentSimulatedDeviceAsync);
    }

    private static void StartsDisabledAndBuildsAllPluginsWithoutAService()
    {
        var session = new DebugSession();
        TestAssert.False(session.Enabled, "Debug builds must start with simulation disabled.");
        TestAssert.Throws<InvalidOperationException>(() => session.Compose(DateTimeOffset.UtcNow));
        foreach (var plugin in session.Plugins)
        {
            plugin.ModeIndex = (int)DebugSourceMode.Simulated;
        }

        session.Enabled = true;
        var frame = session.Compose(DateTimeOffset.UtcNow);
        TestAssert.Equal(4, frame.Catalog.Plugins.Length);
        TestAssert.Equal(12, frame.Snapshot.Sensors.Count(reading => reading.Identity.Component == SensorComponent.PsuPin));
        TestAssert.Equal(frame.Snapshot.Sensors.Length, frame.Snapshot.Sensors.Select(reading => reading.Identity.Key).Distinct().Count());
    }

    private static void EditsEveryMetricWithoutLosingItsIdentity()
    {
        var session = Simulated();
        foreach (var plugin in session.Plugins)
        {
            foreach (var metric in plugin.Metrics)
            {
                metric.ModeIndex = (int)DebugMetricMode.Fixed;
                metric.Value = metric.Definition.IsBoolean ? 1 : 17;
                var frame = session.Compose(DateTimeOffset.UtcNow);
                var reading = frame.Snapshot.Sensors.Single(reading => reading.Identity.PluginId == plugin.Id && metric.Definition.Matches(reading.Identity));
                TestAssert.Equal<double?>(metric.Value, reading.Value);
                TestAssert.Equal(metric.Definition.Unit, reading.Unit);
                TestAssert.Equal(plugin.DeviceId, reading.Identity.DeviceId);
            }
        }
    }

    private static void OverridesOnlySelectedMetricsAndPreservesTheLiveFrame()
    {
        var now = DateTimeOffset.UtcNow;
        var session = Live(now);
        var metric = Pin(session.Plugins[1], SensorMetric.Current, 1);
        metric.ModeIndex = (int)DebugMetricMode.Fixed;
        metric.Value = 9.75;
        session.Plugins[1].ModeIndex = (int)DebugSourceMode.Override;
        var frame = session.Compose(now);
        TestAssert.Equal<double?>(9.75, frame.Snapshot.Sensors.Single(reading => reading.Identity.Metric == SensorMetric.Current).Value);
        TestAssert.Equal<double?>(12, frame.Snapshot.Sensors.Single(reading => reading.Identity.Metric == SensorMetric.Voltage).Value);
        session.Plugins[1].ModeIndex = (int)DebugSourceMode.Live;
        frame = session.Compose(now);
        TestAssert.Equal<double?>(5, frame.Snapshot.Sensors.Single(reading => reading.Identity.Metric == SensorMetric.Current).Value);
        session.Enabled = false;
        TestAssert.Throws<InvalidOperationException>(() => session.Compose(now));
    }

    private static void DoesNotRefreshLiveTimestampsOnSimulationTicks()
    {
        var now = DateTimeOffset.UtcNow;
        var session = Live(now);
        session.Plugins[1].ModeIndex = (int)DebugSourceMode.Override;
        var frame = session.Compose(now.AddSeconds(30));
        TestAssert.True(frame.Snapshot.Sensors.All(reading => reading.ObservedAt == now), "Unmodified live readings must retain their original age.");
        var dashboard = new DashboardViewModel();
        dashboard.SetCatalog(frame.Catalog);
        dashboard.SetConfiguration(frame.Configuration);
        dashboard.Apply(frame.Snapshot, new AppPreferences(), now.AddSeconds(30));
        TestAssert.Equal(0, dashboard.ReportingPanels.Count);
    }

    private static void SimulatesPluginInstallationEnablementAndRecovery()
    {
        var session = Simulated();
        var plugin = session.Plugins[2];
        plugin.Installed = false;
        var frame = session.Compose(DateTimeOffset.UtcNow);
        TestAssert.Equal(3, frame.Catalog.Plugins.Length);
        TestAssert.False(frame.Configuration.Configuration.Plugins.ContainsKey(plugin.Id), "Uninstalled plugins must disappear from configuration.");
        plugin.Installed = true;
        plugin.Enabled = false;
        frame = session.Compose(DateTimeOffset.UtcNow);
        TestAssert.Equal(PluginState.Disabled, frame.Snapshot.Plugins.Single(item => item.PluginId == plugin.Id).State);
        TestAssert.False(frame.Snapshot.Sensors.Any(reading => reading.Identity.PluginId == plugin.Id), "Disabled plugins must not publish readings.");
        plugin.Enabled = true;
        plugin.StateIndex = (int)PluginState.NoDevice;
        frame = session.Compose(DateTimeOffset.UtcNow);
        TestAssert.False(frame.Snapshot.Sensors.Any(reading => reading.Identity.PluginId == plugin.Id), "Missing devices must not keep cached readings.");
        plugin.StateIndex = (int)PluginState.Ready;
        TestAssert.Equal(18, session.Compose(DateTimeOffset.UtcNow).Snapshot.Sensors.Count(reading => reading.Identity.PluginId == plugin.Id));
    }

    private static void SupportsMissingStaleAndResetMetrics()
    {
        var session = Simulated();
        var plugin = session.Plugins[2];
        var pin = plugin.Metrics.First(metric => metric.Definition is { Component: SensorComponent.PsuPin, ConnectorId: "2", Channel: 6 });
        pin.ModeIndex = (int)DebugMetricMode.Unavailable;
        var frame = session.Compose(DateTimeOffset.UtcNow);
        TestAssert.Equal<double?>(null, frame.Snapshot.Sensors.Single(reading => pin.Definition.Matches(reading.Identity)).Value);
        pin.ModeIndex = (int)DebugMetricMode.Stale;
        frame = session.Compose(DateTimeOffset.UtcNow);
        TestAssert.Equal(ReadingQuality.Stale, frame.Snapshot.Sensors.Single(reading => pin.Definition.Matches(reading.Identity)).Quality);
        pin.Reset();
        frame = session.Compose(DateTimeOffset.UtcNow);
        TestAssert.Equal(ReadingQuality.Valid, frame.Snapshot.Sensors.Single(reading => pin.Definition.Matches(reading.Identity)).Quality);
        session.ResetPlugin(plugin.Id);
        TestAssert.Equal(DebugSourceMode.Live, plugin.Mode);
    }

    private static void DerivesTotalsAndAllowsIndependentOverrides()
    {
        var session = Simulated();
        var plugin = session.Plugins[0];
        var pin = Pin(plugin, SensorMetric.Current, 1);
        pin.ModeIndex = (int)DebugMetricMode.Fixed;
        pin.Value = 8;
        var frame = session.Compose(DateTimeOffset.UtcNow);
        TestAssert.Equal<double?>(38, frame.Snapshot.Sensors.Single(reading => reading.Identity is { PluginId: "wireview-pro2", Component: SensorComponent.GpuConnector, Metric: SensorMetric.Current }).Value);
        var voltage = plugin.Metrics.Single(metric => metric.Definition is { Component: SensorComponent.GpuConnector, Metric: SensorMetric.Voltage });
        voltage.ModeIndex = (int)DebugMetricMode.Fixed;
        voltage.Value = 11.6;
        frame = session.Compose(DateTimeOffset.UtcNow);
        var panel = new HardwarePanelViewModel(frame.Catalog.Plugins.Single(item => item.Id == plugin.Id));
        panel.Apply(frame.Snapshot, new AppPreferences(), frame.Snapshot.Timestamp);
        TestAssert.True(panel.VoltageText.StartsWith(11.6.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture), StringComparison.Ordinal), "Explicit voltage must be independently displayable.");
    }

    private static void UsesProductionAlertDurationsAndPerPinRecovery()
    {
        var session = Simulated();
        var plugin = session.Plugins[1];
        var first = Pin(plugin, SensorMetric.Current, 1);
        first.ModeIndex = (int)DebugMetricMode.Fixed;
        first.Value = 10;
        var now = DateTimeOffset.UtcNow;
        TestAssert.Equal(0, session.Compose(now).Snapshot.Alerts.Length);
        var frame = session.Compose(now.AddMilliseconds(3100));
        TestAssert.True(frame.Snapshot.Alerts.Any(alert => alert.Kind == AsusAlertKinds.WireOvercurrent), "Sustained pin current must use production timing.");
        TestAssert.True(frame.Snapshot.Alerts.Any(alert => alert.Kind == AsusAlertKinds.CurrentImbalance), "Imbalance must use production timing.");
        first.Value = 6;
        var second = Pin(plugin, SensorMetric.Current, 2);
        second.ModeIndex = (int)DebugMetricMode.Fixed;
        second.Value = 10;
        frame = session.Compose(now.AddMilliseconds(3200));
        TestAssert.False(frame.Snapshot.Alerts.Any(alert => alert.Kind == AsusAlertKinds.WireOvercurrent), "A different pin must start its own duration.");
        second.Value = 6;
        TestAssert.Equal(0, session.Compose(now.AddMilliseconds(3300)).Snapshot.Alerts.Length);
    }

    private static void TriggersGroupedNotificationsAndDeduplicatesServerEvents()
    {
        var session = Simulated();
        var fault = session.Plugins[0].Metrics.Single(metric => metric.Definition.Metric == SensorMetric.FaultStatus);
        fault.ModeIndex = (int)DebugMetricMode.Fixed;
        fault.Value = 8;
        var temperature = session.Plugins.Single(plugin => plugin.Id == "generic-nvidia").Metrics.Single(metric => metric.Definition is { Component: SensorComponent.GpuCore, Metric: SensorMetric.Temperature });
        temperature.ModeIndex = (int)DebugMetricMode.Fixed;
        temperature.Value = 90;
        var now = DateTimeOffset.UtcNow;
        var tracker = new WarningTransitionTracker();
        TestAssert.Equal(1, tracker.Evaluate(session.Compose(now).Snapshot, now).Length);
        TestAssert.Equal(0, tracker.Evaluate(session.Compose(now.AddSeconds(1)).Snapshot, now.AddSeconds(1)).Length);
        fault.Value = 0;
        temperature.Value = 60;
        TestAssert.Equal(0, tracker.Evaluate(session.Compose(now.AddSeconds(2)).Snapshot, now.AddSeconds(2)).Length);
    }

    private static void OtherPluginEditsDoNotResetAsusTimers()
    {
        var session = Simulated();
        var first = Pin(session.Plugins[1], SensorMetric.Current, 1);
        first.ModeIndex = (int)DebugMetricMode.Fixed;
        first.Value = 10;
        var now = DateTimeOffset.UtcNow;
        session.Compose(now);
        session.Plugins[2].Installed = false;
        var frame = session.Compose(now.AddMilliseconds(3100));
        TestAssert.True(frame.Snapshot.Alerts.Any(alert => alert.Kind == AsusAlertKinds.WireOvercurrent), "Editing PSU discovery must not reset the GPU's sustained-current timer.");
    }

    private static void RejectsInvalidValuesAndKeepsFiniteReadings()
    {
        var session = Simulated();
        var metric = Pin(session.Plugins[0], SensorMetric.Current, 1);
        metric.Value = double.NaN;
        TestAssert.True(metric.Error.Length > 0 && double.IsFinite(metric.Value), "NaN must never reach an effective frame.");
        var boolean = session.Plugins[2].Metrics.Single(item => item.Definition.Metric == SensorMetric.FanAutomatic);
        boolean.Value = 0.5;
        TestAssert.Equal(1d, boolean.Value);
    }

    private static void ResolvesSelectionsAgainstTheEffectiveReadings()
    {
        var session = Simulated();
        var frame = session.Compose(DateTimeOffset.UtcNow);
        var source = frame.Snapshot.Sensors.First(reading => reading.Identity.PluginId == "wireview-pro2").Identity;
        var configuration = frame.Configuration.Configuration with
        {
            Monitoring = new MonitoringOptions { Selections = [new SensorSelection { Name = "Debug source", PreferredSources = [source] }] }
        };
        session.SetConfiguration(configuration, frame.Configuration.Version!);
        var resolved = session.Compose(DateTimeOffset.UtcNow).Snapshot.Selections.Single();
        TestAssert.Equal(source.Key, resolved.SensorKey);
        TestAssert.Equal(SelectionState.Selected, resolved.State);
    }

    private static void ResetAllDiscardsLocalConfigurationAndOverrides()
    {
        var session = Simulated();
        var frame = session.Compose(DateTimeOffset.UtcNow);
        session.SetConfiguration(frame.Configuration.Configuration with { Monitoring = new MonitoringOptions { StreamIntervalMs = 500 } }, frame.Configuration.Version!);
        session.ResetAll();
        TestAssert.False(session.Enabled, "Reset all must disable simulation.");
        TestAssert.True(session.Plugins.All(plugin => plugin.Mode == DebugSourceMode.Live && plugin.Metrics.All(metric => metric.Mode == DebugMetricMode.Live)), "Reset all must clear every override.");
        session.Enabled = true;
        TestAssert.Equal(1000, session.Compose(DateTimeOffset.UtcNow).Configuration.Configuration.Monitoring.StreamIntervalMs);
    }

    private static async Task ConfigurationAndDeviceCommandsRemainLocalAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var session = Live(now);
        var plugin = session.Plugins[2];
        plugin.ModeIndex = (int)DebugSourceMode.Simulated;
        var api = new DebugMonitoringApi(session, TimeProvider.System);
        var original = await api.GetConfigurationAsync(CancellationToken.None);
        var settings = new Dictionary<string, PluginSettings>(original.Configuration.Plugins)
        {
            [plugin.Id] = new() { Options = JsonSerializer.SerializeToElement(new MsiOptions { FanControl = MsiFanControl.Manual, FanDutyPercent = 75 }, ClientJsonContext.Default.MsiOptions) }
        };
        await api.SetConfigurationAsync(original.Configuration with { Plugins = settings }, original.Version!, CancellationToken.None);
        var snapshot = session.Compose(now).Snapshot;
        TestAssert.Equal<double?>(75, snapshot.Sensors.Single(reading => reading.Identity.PluginId == plugin.Id && reading.Identity.Metric == SensorMetric.FanDuty).Value);
        await api.RestartServiceAsync(CancellationToken.None);
        await api.RestartPluginAsync(plugin.Id, CancellationToken.None);
        await api.ClearWireViewFaultsAsync(new ClearFaultsRequest { FaultStatusMask = ushort.MaxValue }, CancellationToken.None);
        TestAssert.Equal("debug.invalid", api.Address.Host);
        TestAssert.Throws<InvalidOperationException>(() => session.SetConfiguration(original.Configuration, original.Version!));
    }

    private static async Task WireViewSettingsAreEditableOnlyForTheCurrentSimulatedDeviceAsync()
    {
        var session = Simulated();
        var wireView = session.Plugins.Single(plugin => plugin.Id == "wireview-pro2");
        var api = new DebugMonitoringApi(session, TimeProvider.System);
        var initial = await api.GetWireViewConfigurationAsync(CancellationToken.None);
        var display = initial.Display ?? throw new InvalidOperationException("The editor requires a complete display configuration.");

        TestAssert.Throws<ArgumentException>(() => session.ApplySimulatedWireViewConfiguration(
            initial with { WireOcpFaultThresholdA = 9.25 }));

        var changes = initial with { FriendlyName = "Edited in simulation", Display = display with { Rotation = 1 } };
        var saved = await api.ApplyWireViewConfigurationAsync(changes, CancellationToken.None);

        TestAssert.Equal(changes.FriendlyName, saved.FriendlyName);
        TestAssert.Equal(changes.Display, saved.Display);
        var reloaded = await api.GetWireViewConfigurationAsync(CancellationToken.None);
        TestAssert.Equal(saved, reloaded);
        TestAssert.False(ReferenceEquals(saved, reloaded), "Refresh must return a new snapshot so the editor discards unsaved edits.");
        TestAssert.Throws<InvalidOperationException>(() => session.ApplySimulatedWireViewConfiguration(changes));

        wireView.ModeIndex = (int)DebugSourceMode.Override;
        TestAssert.Equal(null, session.GetSimulatedWireViewConfiguration());
        TestAssert.Throws<InvalidOperationException>(() => session.ApplySimulatedWireViewConfiguration(saved));

        wireView.ModeIndex = (int)DebugSourceMode.Simulated;
        wireView.DeviceId = "debug-wireview-replacement";
        TestAssert.Throws<InvalidOperationException>(() => session.ApplySimulatedWireViewConfiguration(saved));

        session.Enabled = false;
        session.Enabled = true;
        var restarted = await api.GetWireViewConfigurationAsync(CancellationToken.None);
        TestAssert.Equal(initial.FriendlyName, restarted.FriendlyName);
        TestAssert.Equal(display, restarted.Display);
        TestAssert.True(initial.EditToken != restarted.EditToken, "A new simulation must reject drafts from the previous session.");
    }

    private static DebugSession Simulated()
    {
        var session = new DebugSession();
        foreach (var plugin in session.Plugins)
        {
            plugin.ModeIndex = (int)DebugSourceMode.Simulated;
        }

        session.Enabled = true;
        return session;
    }

    private static DebugSession Live(DateTimeOffset now)
    {
        var session = new DebugSession();
        session.ReceiveCatalog(new PluginCatalog(1, [new PluginInfo("asus-astral", "Astral", true, PluginState.Ready, now, null, [])]));
        session.ReceiveConfiguration(new ConfigurationSnapshot(1, new ServiceConfiguration { Plugins = new Dictionary<string, PluginSettings> { ["asus-astral"] = new() } }));
        session.ReceiveSnapshot(new SensorSnapshot(1, 1, now, [
            new SensorReading(new SensorIdentity("asus-astral", "gpu", SensorComponent.GpuPin, SensorMetric.Current, 1), SensorUnit.Amperes, 5, now),
            new SensorReading(new SensorIdentity("asus-astral", "gpu", SensorComponent.GpuPin, SensorMetric.Voltage, 1), SensorUnit.Volts, 12, now)],
            [new PluginHealth("asus-astral", PluginState.Ready, now, null)], []));
        session.Enabled = true;
        return session;
    }

    private static DebugMetricViewModel Pin(DebugPluginViewModel plugin, SensorMetric metric, int channel) => plugin.Metrics.Single(item =>
        item.Definition is { Component: SensorComponent.GpuPin } && item.Definition.Metric == metric && item.Definition.Channel == channel);
}
