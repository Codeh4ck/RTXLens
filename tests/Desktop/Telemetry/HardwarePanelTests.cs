using System.Globalization;
using RTXLens.Contracts.Discovery;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Plugins.GenericNvidia.Contracts.Devices;
using RTXLens.Presentation.Settings;
using RTXLens.Presentation.Telemetry;
using RTXLens.Tests.Infrastructure;
using RTXLens.UI.Tests.Fixtures;

namespace RTXLens.UI.Tests.Telemetry;

internal static class HardwarePanelTests
{
    public static void Run(TestRunner runner)
    {
        runner.Run(TracksSelectionIndependentlyOfVisualContainers);
        runner.Run(DiscoveryDoesNotSelectHardwareOverAnotherPage);
        runner.Run(DescribesCurrentSpreadAndItsPins);
        runner.Run(ColorsReadyWarningAndErrorStatusesSemantically);
        runner.Run(HeadroomReportsLimitAndMissingValues);
        runner.Run(PeakResetVisibilityFollowsSettings);
        runner.Run(OnlyReportingPluginsHaveTabs);
        runner.Run(DisabledPluginsDoNotRaiseMissingDeviceWarnings);
        runner.Run(StaleOrEmptyPluginsLoseTheirTabs);
        runner.Run(GroupsGpuStatusAndPromotesHeadlineMetrics);
        runner.Run(ShowsPcieRatesByLinkGeneration);
        runner.Run(ShowsBoostLimitTimesOnlyWhenReported);
        runner.Run(UnavailablePinFlagsDoNotCreateAnEmptyChart);
    }

    private static void TracksSelectionIndependentlyOfVisualContainers()
    {
        var dashboard = new DashboardViewModel();
        dashboard.SetCatalog(new PluginCatalog(1, [UiTestData.Plugin, UiTestData.Plugin with { Id = "wireview-pro2" }]));
        dashboard.SelectedPanel = dashboard.Panels[0];
        TestAssert.True(dashboard.Panels[0].IsSelected, "A newly rendered tab must inherit active selection.");
        dashboard.SelectedPanel = dashboard.Panels[1];
        TestAssert.False(dashboard.Panels[0].IsSelected, "The previous tab must lose selection.");
        TestAssert.True(dashboard.Panels[1].IsSelected, "The active tab must stay selected.");
    }

    private static void DiscoveryDoesNotSelectHardwareOverAnotherPage()
    {
        var dashboard = new DashboardViewModel();
        var catalog = new PluginCatalog(1, [UiTestData.Plugin]);
        dashboard.SetCatalog(catalog);
        dashboard.SelectedPanel = dashboard.Panels[0];
        dashboard.SelectedPanel = null;
        dashboard.SetCatalog(catalog);
        TestAssert.False(dashboard.Panels[0].IsSelected, "Configuration/settings navigation must remain unselected after metadata refresh.");
    }

    private static void DescribesCurrentSpreadAndItsPins()
    {
        var frame = UiTestData.Frame();
        frame = frame with { Sensors = [frame.Sensors[0] with { Value = 4 }, new SensorReading(UiTestData.Pin with { Channel = 6 }, SensorUnit.Amperes, 7.5, frame.Timestamp)] };
        var panel = new HardwarePanelViewModel(UiTestData.Plugin);
        panel.Apply(frame, new AppPreferences(), frame.Timestamp);
        TestAssert.Equal(3.5.ToString("0.##", CultureInfo.CurrentCulture) + " A", panel.CurrentSpreadText);
        TestAssert.True(panel.HighestCurrentText.StartsWith("P6", StringComparison.Ordinal), "The highest pin must be explicit.");
        TestAssert.True(panel.LowestCurrentText.StartsWith("P1", StringComparison.Ordinal), "The lowest pin must be explicit.");
        panel.MarkDisconnected(new AppPreferences());
        TestAssert.Equal("—", panel.CurrentSpreadText);
    }

    private static void ColorsReadyWarningAndErrorStatusesSemantically()
    {
        TestAssert.Equal(StatusTone.Positive, new HardwarePanelViewModel(UiTestData.Plugin).Tone);
        TestAssert.Equal(StatusTone.Caution, new HardwarePanelViewModel(UiTestData.Plugin with { State = PluginState.Disabled }).Tone);
        TestAssert.Equal(StatusTone.Caution, new HardwarePanelViewModel(UiTestData.Plugin with { State = PluginState.Stale }).Tone);
        TestAssert.Equal(StatusTone.Negative, new HardwarePanelViewModel(UiTestData.Plugin with { State = PluginState.Error }).Tone);
    }

    private static void HeadroomReportsLimitAndMissingValues()
    {
        var pin = new PinViewModel(1);
        pin.Update(5, 12, 60, PinMetric.Current, 9.2, false, true);
        TestAssert.Equal(StatusTone.Positive, pin.HeadroomTone);
        pin.Update(10, 12, 120, PinMetric.Current, 9.2, false, true);
        TestAssert.Equal(StatusTone.Negative, pin.HeadroomTone);
        pin.Update(null, null, null, PinMetric.Current, 9.2, false, true);
        TestAssert.Equal(StatusTone.Neutral, pin.HeadroomTone);
        TestAssert.Equal("—", pin.Headroom);
    }

    private static void PeakResetVisibilityFollowsSettings()
    {
        var panel = new HardwarePanelViewModel(UiTestData.Plugin);
        var frame = UiTestData.Frame();
        panel.Apply(frame, new AppPreferences(), frame.Timestamp);
        TestAssert.False(panel.ShowPeakHold, "Reset should be hidden by default.");
        panel.Apply(frame, new AppPreferences { ShowPeakHold = true }, frame.Timestamp);
        TestAssert.True(panel.ShowPeakHold, "Reset should be available when peak hold is enabled.");
    }

    private static void OnlyReportingPluginsHaveTabs()
    {
        var dashboard = new DashboardViewModel();
        dashboard.SetCatalog(new PluginCatalog(1, [UiTestData.Plugin, UiTestData.Plugin with { Id = "wireview-pro2", State = PluginState.NoDevice }]));
        var frame = UiTestData.Frame();
        dashboard.Apply(frame, new AppPreferences(), frame.Timestamp);
        TestAssert.Equal(2, dashboard.Panels.Count);
        TestAssert.Equal("asus-astral", dashboard.ReportingPanels.Single().Id);
        TestAssert.True(dashboard.PluginWarning.Contains("WireView", StringComparison.Ordinal), "Missing enabled hardware must have a named warning.");
        dashboard.SelectedPanel = dashboard.ReportingPanels[0];
        dashboard.Apply(frame with { Plugins = [frame.Plugins[0] with { State = PluginState.NoDevice }] }, new AppPreferences(), frame.Timestamp);
        TestAssert.Equal(0, dashboard.ReportingPanels.Count);
        TestAssert.Equal<HardwarePanelViewModel?>(null, dashboard.SelectedPanel);
        dashboard.Apply(frame, new AppPreferences(), frame.Timestamp);
        TestAssert.Equal(1, dashboard.ReportingPanels.Count);
    }

    private static void DisabledPluginsDoNotRaiseMissingDeviceWarnings()
    {
        var dashboard = new DashboardViewModel();
        dashboard.SetCatalog(new PluginCatalog(1, [UiTestData.Plugin with { Enabled = false, State = PluginState.Disabled }]));
        var frame = UiTestData.Frame();
        dashboard.Apply(frame, new AppPreferences(), frame.Timestamp);
        TestAssert.Equal(0, dashboard.ReportingPanels.Count);
        TestAssert.False(dashboard.HasPluginWarning, "Disabled plugins must be hidden without warnings even if the last frame was Ready.");
    }

    private static void StaleOrEmptyPluginsLoseTheirTabs()
    {
        var dashboard = new DashboardViewModel();
        dashboard.SetCatalog(new PluginCatalog(1, [UiTestData.Plugin]));
        var frame = UiTestData.Frame();
        dashboard.Apply(frame with { Sensors = [] }, new AppPreferences(), frame.Timestamp);
        TestAssert.Equal(0, dashboard.ReportingPanels.Count);
        dashboard.Apply(frame, new AppPreferences(), frame.Timestamp);
        TestAssert.Equal(1, dashboard.ReportingPanels.Count);
        dashboard.RefreshFreshness(frame.Timestamp.AddSeconds(6), new AppPreferences());
        TestAssert.Equal(0, dashboard.ReportingPanels.Count);
        TestAssert.True(dashboard.HasPluginWarning, "Stale plugins must be named in the warning.");
    }

    private static void GroupsGpuStatusAndPromotesHeadlineMetrics()
    {
        var frame = UiTestData.Frame();
        frame = frame with { Sensors = [
            Reading(SensorComponent.GpuFan, SensorMetric.FanSpeed, 1200, 1), Reading(SensorComponent.GpuFan, SensorMetric.FanSpeed, 1400, 2),
            Reading(SensorComponent.GpuCore, SensorMetric.Temperature, 85), Reading(SensorComponent.GpuMemory, SensorMetric.Temperature, 70),
            Reading(SensorComponent.GpuMemory, SensorMetric.MemoryUsed, 8192), Reading(SensorComponent.GpuMemory, SensorMetric.MemoryTotal, 32768),
            Reading(SensorComponent.GpuMemory, SensorMetric.Utilization, 25), Reading(SensorComponent.GpuCore, SensorMetric.Utilization, 80),
            Reading(SensorComponent.GpuBoard, SensorMetric.Power, 450)] };
        frame = frame with
        {
            Sensors = [.. frame.Sensors.Select(reading => reading with { Identity = reading.Identity with { PluginId = "generic-nvidia" } })],
            Plugins = [new Contracts.Health.PluginHealth("generic-nvidia", PluginState.Ready, frame.Timestamp, "RTX 5090")]
        };
        var model = new GpuStatusViewModel();
        model.Apply(frame, null, new AppPreferences(), frame.Timestamp);
        TestAssert.Equal(5, model.Metrics.Count);
        TestAssert.Equal("VRAM usage", model.Metrics[2].Label);
        TestAssert.Equal("Core / VRAM temp", model.Metrics[3].Label);
        TestAssert.Equal("1200 RPM / 1400 RPM", model.Metrics.Single(metric => metric.Label == "Fan 1 / 2").Value);
        TestAssert.Equal("80%", model.GpuUsageText);
        TestAssert.False(model.Metrics[3].IsWarning, "Warning colors must consume the shared backend state, not infer independent thresholds.");
        model.Clear();
        TestAssert.True(model.IsUnavailable, "Disconnected GPU status must be explicit.");
        return;

        SensorReading Reading(SensorComponent component, SensorMetric metric, double value, int? channel = null) =>
            new(UiTestData.Pin with { Component = component, Metric = metric, Channel = channel }, SensorUnits.For(metric), value, frame.Timestamp);
    }

    private static void ShowsPcieRatesByLinkGeneration()
    {
        var diagnostics = ApplyGpuDiagnostics(
            (SensorComponent.GpuPcie, SensorMetric.LinkGeneration, null, 1),
            (SensorComponent.GpuPcie, SensorMetric.LinkWidth, null, 8),
            (SensorComponent.GpuPcie, SensorMetric.LinkSpeed, null, 32000),
            (SensorComponent.GpuPcie, SensorMetric.MaximumSystemLinkGeneration, null, 5),
            (SensorComponent.GpuPcie, SensorMetric.MaximumLinkWidth, null, 16),
            (SensorComponent.GpuPcie, SensorMetric.MaximumLinkSpeed, null, 5),
            (SensorComponent.GpuPcie, SensorMetric.ReceiveThroughput, null, 1024));

        var gen1Rate = 2.5.ToString("0.#", CultureInfo.CurrentCulture);
        TestAssert.Equal($"Gen 1 ×8 · {gen1Rate} GT/s per lane", diagnostics.Pcie.Single(metric => metric.Label == "PCIe Link Speed").Value);
        TestAssert.Equal("Gen 5 ×16 · 32 GT/s per lane", diagnostics.Pcie.Single(metric => metric.Label == "Max PCIe Link Speed").Value);
        TestAssert.Equal("1 KiB/s", diagnostics.Pcie.Single(metric => metric.Label == "PCIe RX").Value);
    }

    private static void ShowsBoostLimitTimesOnlyWhenReported()
    {
        var diagnostics = ApplyGpuDiagnostics(
            (SensorComponent.GpuPerformance, SensorMetric.ActiveClockReasons, null, (double)NvidiaClockReason.Reliability),
            (SensorComponent.GpuPerformance, SensorMetric.SupportedClockReasons, null,
                (double)(NvidiaClockReason.SoftwarePowerCap | NvidiaClockReason.Reliability)),
            (SensorComponent.GpuPerformance, SensorMetric.ClockLimitDuration, (int)NvidiaClockReason.SoftwarePowerCap, 0.22),
            (SensorComponent.GpuPerformance, SensorMetric.ClockLimitDuration, (int)NvidiaClockReason.SyncBoost, 0));

        var duration = 0.22.ToString("0.###", CultureInfo.CurrentCulture);
        TestAssert.Equal(3, diagnostics.BoostLimits.Count);
        TestAssert.Equal($"{duration} s", diagnostics.BoostLimits.Single(metric => metric.Label == "Software power cap").Value);
        TestAssert.Equal("0 s", diagnostics.BoostLimits.Single(metric => metric.Label == "Sync Boost").Value);
        TestAssert.Equal("-", diagnostics.BoostLimits.Single(metric => metric.Label == "Reliability").Value);
    }

    private static GpuDiagnosticsViewModel ApplyGpuDiagnostics(
        params (SensorComponent Component, SensorMetric Metric, int? Channel, double Value)[] readings)
    {
        var frame = UiTestData.Frame();
        frame = frame with
        {
            Sensors = [.. readings.Select(reading => new SensorReading(
                UiTestData.Pin with
                {
                    PluginId = GpuStatusViewModel.PluginId,
                    Component = reading.Component,
                    Metric = reading.Metric,
                    Channel = reading.Channel
                },
                SensorUnits.For(reading.Metric), reading.Value, frame.Timestamp))],
            Plugins = [new Contracts.Health.PluginHealth(GpuStatusViewModel.PluginId, PluginState.Ready, frame.Timestamp, "RTX 5090")]
        };

        var model = new GpuStatusViewModel();
        model.Apply(frame, null, new AppPreferences(), frame.Timestamp);
        return model.Diagnostics;
    }

    private static void UnavailablePinFlagsDoNotCreateAnEmptyChart()
    {
        var frame = UiTestData.Frame();
        frame = frame with { Sensors = [
            new SensorReading(UiTestData.Pin with { Component = SensorComponent.GpuCore, Metric = SensorMetric.Clock }, SensorUnit.Megahertz, 2000, frame.Timestamp),
            new SensorReading(UiTestData.Pin with { Metric = SensorMetric.OvercurrentDetected }, SensorUnit.Boolean, null, frame.Timestamp, ReadingQuality.Unavailable)] };
        var panel = new HardwarePanelViewModel(UiTestData.Plugin);
        panel.Apply(frame, new AppPreferences(), frame.Timestamp);
        TestAssert.False(panel.HasPins, "Unavailable protection flags must not create a chart without pin readings.");
        TestAssert.True(panel.IsReporting, "General GPU telemetry must keep the Astral tab available without pin readings.");
    }
}
