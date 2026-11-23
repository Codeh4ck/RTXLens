using RTXLens.Contracts.Discovery;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Plugins.WireView.Contracts.Common;
using RTXLens.Presentation.Settings;
using RTXLens.Presentation.Telemetry;
using RTXLens.Tests.Infrastructure;
using RTXLens.UI.Tests.Fixtures;

namespace RTXLens.UI.Tests.Telemetry;

internal static class WireViewPanelTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        runner.Run(ClearFaultActionTracksCapabilitiesFaultsAndConnection);
        runner.Run(GroupsTemperaturesAndPutsCableRatingFirst);
        runner.Run(ShowsReadableActiveAndLoggedFaultsWithoutMasks);
        runner.Run(FaultRecoveryAndDisconnectClearWarnings);
        runner.Run(MissingFaultReadingsRemainUnavailable);
        runner.Run(FormatsDeviceSettingsAndClearsThemAfterDeviceLoss);
        await runner.RunAsync(LoadsHeadroomAfterTransientFailureAsync);
        await runner.RunAsync(IgnoresSupersededDeviceReadsAsync);
    }

    private static void ClearFaultActionTracksCapabilitiesFaultsAndConnection()
    {
        var (panel, healthy) = Create(0, 0);
        var info = UiTestData.Plugin with
        {
            Id = "wireview-pro2",
            Capabilities = [PluginCapability.Telemetry, PluginCapability.FaultClear]
        };
        panel.UpdateDiscovery(info);
        panel.Apply(healthy, new AppPreferences(), healthy.Timestamp);
        TestAssert.False(panel.ShowClearFaults, "Healthy hardware must not show Clear faults.");

        var changes = 0;
        panel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(panel.ShowClearFaults))
            {
                changes++;
            }
        };
        var (_, logged) = Create(0, 0x04);
        panel.Apply(logged, new AppPreferences(), logged.Timestamp);
        TestAssert.True(panel.ShowClearFaults, "Logged faults must be clearable.");
        TestAssert.True(changes > 0, "Fault visibility must update live.");

        panel.UpdateDiscovery(info with { Capabilities = [PluginCapability.Telemetry] });
        TestAssert.False(panel.ShowClearFaults, "The action requires device support.");
        panel.UpdateDiscovery(info);
        TestAssert.True(panel.ShowClearFaults, "Restoring device support must restore the action.");
        panel.MarkDisconnected(new AppPreferences());
        TestAssert.False(panel.ShowClearFaults, "Disconnected devices must not show Clear faults.");

        var (_, active) = Create(0x02, 0);
        panel.Apply(active, new AppPreferences(), active.Timestamp);
        TestAssert.True(panel.ShowClearFaults, "Active faults must be clearable.");
        panel.Apply(healthy, new AppPreferences(), healthy.Timestamp);
        TestAssert.False(panel.ShowClearFaults, "The action must disappear when faults clear.");
    }

    private static void GroupsTemperaturesAndPutsCableRatingFirst()
    {
        var (panel, frame) = Create(0, 0);
        panel.Apply(frame, new AppPreferences(), frame.Timestamp);
        TestAssert.Equal("Cable rating", panel.Metrics[0].Label);
        TestAssert.Equal("600 W", panel.Metrics[0].Value);
        TestAssert.Equal("40 °C / 42 °C", Metric(panel, "Board Temp In/Out").Value);
        TestAssert.Equal("35 °C / 36 °C", Metric(panel, "External Temp 1/2").Value);
        TestAssert.Equal("OK", Metric(panel, "Fault Status").Value);
        TestAssert.Equal(StatusTone.Positive, Metric(panel, "Fault Log Status").Tone);
        TestAssert.Equal(5, panel.Metrics.Count);
        panel.Apply(frame, new AppPreferences { Fahrenheit = true }, frame.Timestamp);
        TestAssert.True(Metric(panel, "Board Temp In/Out").Value.Contains("°F", StringComparison.Ordinal), "Paired temperatures must honor the temperature unit preference.");
    }

    private static void ShowsReadableActiveAndLoggedFaultsWithoutMasks()
    {
        var (panel, frame) = Create(0x06, 0x84);
        panel.Apply(frame, new AppPreferences(), frame.Timestamp);
        TestAssert.True(panel.HasFaults, "Active and logged faults must be exposed as warnings.");
        TestAssert.Equal("Active", panel.Faults.Single(metric => metric.Label == "Overtemperature").Value);
        TestAssert.Equal("Active / Logged", panel.Faults.Single(metric => metric.Label == "Overcurrent").Value);
        TestAssert.Equal("Logged", panel.Faults.Single(metric => metric.Label == "Unrecognized device fault").Value);
        TestAssert.True(panel.Faults.All(metric => metric is { Tone: StatusTone.Negative, IconName: "overcurrent" }), "Every fault must use the negative tone and fault icon.");
        TestAssert.True(panel.Metrics.Concat(panel.Faults).All(metric => !metric.Value.Contains("0x", StringComparison.Ordinal)), "No raw fault masks should be displayed.");
        TestAssert.Equal(StatusTone.Negative, Metric(panel, "Fault Status").Tone);
        TestAssert.Equal(StatusTone.Negative, Metric(panel, "Fault Log Status").Tone);
        var firstFault = panel.Faults[0];
        panel.Apply(frame, new AppPreferences(), frame.Timestamp);
        TestAssert.True(ReferenceEquals(firstFault, panel.Faults[0]), "Unchanged fault flags must reuse their warning rows.");
    }

    private static void FaultRecoveryAndDisconnectClearWarnings()
    {
        var (panel, faulted) = Create(0x08, 0x08);
        panel.Apply(faulted, new AppPreferences(), faulted.Timestamp);
        var (_, recovered) = Create(0, 0);
        panel.Apply(recovered, new AppPreferences(), recovered.Timestamp);
        TestAssert.False(panel.HasFaults, "Cleared flags must remove individual warnings.");
        TestAssert.Equal("OK", Metric(panel, "Fault Status").Value);
        panel.Apply(faulted, new AppPreferences(), faulted.Timestamp);
        panel.MarkDisconnected(new AppPreferences());
        TestAssert.Equal(0, panel.Faults.Count);
        TestAssert.Equal(StatusTone.Caution, Metric(panel, "Fault Status").Tone);
    }

    private static void MissingFaultReadingsRemainUnavailable()
    {
        var (panel, frame) = Create(0x02, 0x02);
        panel.Apply(frame, new AppPreferences(), frame.Timestamp);
        frame = frame with { Sensors =
            [.. frame.Sensors.Where(reading => reading.Identity.Metric is not SensorMetric.FaultStatus and not SensorMetric.FaultLog)]
        };
        panel.Apply(frame, new AppPreferences(), frame.Timestamp);
        TestAssert.Equal("Unavailable", Metric(panel, "Fault Status").Value);
        TestAssert.Equal("Unavailable", Metric(panel, "Fault Log Status").Value);
        TestAssert.False(panel.HasFaults, "Missing readings must not retain previous fault warnings or claim OK.");
    }

    private static void FormatsDeviceSettingsAndClearsThemAfterDeviceLoss()
    {
        var (panel, frame) = Create(0, 0);
        panel.Apply(frame, new AppPreferences(), frame.Timestamp);
        panel.SetDeviceConfiguration(new DeviceConfig
        {
            FriendlyName = "Test WireView", FanDutyMin = 20, FanDutyMax = 100, FanTempMinC = 30, FanTempMaxC = 60,
            WireOcpFaultThresholdA = 9.2, FaultDisplayEnable = 0x06
        });
        TestAssert.Equal("20 % / 100 %", panel.DeviceDetails.Single(metric => metric.Label == "Fan duty min / max").Value);
        TestAssert.Equal("30 °C / 60 °C", panel.DeviceDetails.Single(metric => metric.Label == "Fan temperature min / max").Value);
        TestAssert.Equal("Overtemperature, Overcurrent", panel.DeviceDetails.Single(metric => metric.Label == "Fault display").Value);
        TestAssert.Equal("Unavailable", panel.DeviceDetails.Single(metric => metric.Label == "Averaging").Value);
        TestAssert.Equal(20, panel.DeviceDetails.Count);
        panel.MarkDisconnected(new AppPreferences());
        TestAssert.Equal(0, panel.DeviceDetails.Count);
        TestAssert.True(panel.DeviceConfigurationStatus.Contains("unavailable", StringComparison.Ordinal), "The settings window must explain that the device is unavailable.");
    }

    private static async Task LoadsHeadroomAfterTransientFailureAsync()
    {
        var (panel, frame) = Create(0, 0);
        var prefs = new AppPreferences();
        var pin = frame.Sensors[0].Identity with { Component = SensorComponent.GpuPin, Metric = SensorMetric.Current, Channel = 1 };
        frame = frame with { Sensors = [new SensorReading(pin, SensorUnit.Amperes, 6, frame.Timestamp)] };
        panel.Apply(frame, prefs, frame.Timestamp);
        var reads = 0;
        var response = new TaskCompletionSource<DeviceConfig>();

        await panel.EnsureDeviceConfigurationAsync(Read, frame.Timestamp, CancellationToken.None);
        await panel.EnsureDeviceConfigurationAsync(Read, frame.Timestamp.AddSeconds(1), CancellationToken.None);
        TestAssert.Equal(1, reads);
        var retryTime = frame.Timestamp.AddSeconds(20);
        var retry = panel.EnsureDeviceConfigurationAsync(Read, retryTime, CancellationToken.None);
        await panel.EnsureDeviceConfigurationAsync(Read, retryTime, CancellationToken.None);
        response.SetResult(new DeviceConfig { WireOcpFaultThresholdA = 9 });
        await retry;
        await panel.EnsureDeviceConfigurationAsync(Read, retryTime, CancellationToken.None);
        panel.Apply(frame, prefs, frame.Timestamp);
        TestAssert.Equal(2, reads);
        TestAssert.Equal("+3 A", panel.Pins[0].Headroom);

        panel.MarkDisconnected(prefs);
        panel.Apply(frame, prefs, frame.Timestamp);
        TestAssert.Equal("—", panel.Pins[0].Headroom);
        return;

        Task<DeviceConfig> Read(CancellationToken _) => ++reads == 1
            ? Task.FromException<DeviceConfig>(new HttpRequestException("Device starting"))
            : response.Task;
    }

    private static async Task IgnoresSupersededDeviceReadsAsync()
    {
        var (panel, frame) = Create(0, 0);
        var prefs = new AppPreferences();
        panel.Apply(frame, prefs, frame.Timestamp);
        var original = new TaskCompletionSource<DeviceConfig>();
        var firstRead = panel.EnsureDeviceConfigurationAsync(_ => original.Task, frame.Timestamp, CancellationToken.None);
        panel.MarkDisconnected(prefs);
        panel.Apply(frame, prefs, frame.Timestamp);
        var current = new TaskCompletionSource<DeviceConfig>();
        var secondRead = panel.EnsureDeviceConfigurationAsync(_ => current.Task, frame.Timestamp, CancellationToken.None);

        original.SetResult(new DeviceConfig { WireOcpFaultThresholdA = 8 });
        await firstRead;
        TestAssert.True(panel.DeviceConfiguration is null, "A response from before reconnect must be ignored.");
        panel.SetDeviceConfiguration(new DeviceConfig { WireOcpFaultThresholdA = 10 });
        current.SetResult(new DeviceConfig { WireOcpFaultThresholdA = 9 });
        await secondRead;
        TestAssert.Equal(10d, panel.DeviceConfiguration!.WireOcpFaultThresholdA);
    }

    private static MetricViewModel Metric(HardwarePanelViewModel panel, string label) => panel.Metrics.Single(metric => metric.Label == label);

    private static (HardwarePanelViewModel Panel, SensorSnapshot Frame) Create(ushort status, ushort log)
    {
        var frame = UiTestData.Frame();
        var identity = UiTestData.Pin with { PluginId = "wireview-pro2", DeviceId = "com3" };
        frame = frame with
        {
            Plugins = [frame.Plugins[0] with { PluginId = "wireview-pro2" }],
            Sensors = [
                Reading(SensorComponent.MonitorBoard, SensorMetric.Temperature, 40, 1),
                Reading(SensorComponent.MonitorBoard, SensorMetric.Temperature, 42, 2),
                Reading(SensorComponent.ExternalProbe, SensorMetric.Temperature, 35, 1),
                Reading(SensorComponent.ExternalProbe, SensorMetric.Temperature, 36, 2),
                Reading(SensorComponent.GpuConnector, SensorMetric.PowerCapability, 600, 1),
                Reading(SensorComponent.MonitorBoard, SensorMetric.FaultStatus, status),
                Reading(SensorComponent.MonitorBoard, SensorMetric.FaultLog, log)]
        };
        return (new HardwarePanelViewModel(UiTestData.Plugin with { Id = "wireview-pro2", Capabilities = [PluginCapability.Telemetry, PluginCapability.DeviceConfigurationRead] }), frame);

        SensorReading Reading(SensorComponent component, SensorMetric metric, double value, int? channel = null) =>
            new(identity with { Component = component, Metric = metric, Channel = channel }, SensorUnits.For(metric), value, frame.Timestamp);
    }
}
