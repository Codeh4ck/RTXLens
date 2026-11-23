using System.Text.Json;
using RTXLens.Client.Serialization;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Discovery;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Health;
using RTXLens.Contracts.Sensors;
using RTXLens.Plugins.Msi.Contracts.Configuration;
using RTXLens.Presentation.Configuration;
using RTXLens.Presentation.Settings;
using RTXLens.Presentation.Telemetry;
using RTXLens.Tests.Infrastructure;
using RTXLens.UI.Tests.TestDoubles;

namespace RTXLens.UI.Tests.Telemetry;

internal static class MsiPanelTests
{
    public static void Run(TestRunner runner)
    {
        runner.Run(KeepsBothConnectorChartsDistinctAndCurrentOnly);
        runner.Run(TracksFanDraftsAndValidatesPolling);
        runner.Run(EditsCurvePointsAndPreservesSavedSettings);
        runner.Run(LimitsCurvePointsAndAllowsFallingDuty);
        runner.Run(SnapsLowDutyToAutomatic);
        runner.RunAsync(SynchronizesSavedCurvesWithoutDiscardingOtherDraftsAsync).GetAwaiter().GetResult();
    }

    private static void KeepsBothConnectorChartsDistinctAndCurrentOnly()
    {
        var now = DateTimeOffset.UtcNow;
        var panel = new HardwarePanelViewModel(new PluginInfo("msi-psu", "MSI PSU", true, PluginState.Ready, now, null, []));
        var sensors = new List<SensorReading>();
        for (var connector = 1; connector <= 2; connector++)
            for (var pin = 1; pin <= 6; pin++)
        {
            sensors.Add(new SensorReading(new SensorIdentity("msi-psu", "psu", SensorComponent.PsuPin, SensorMetric.Current,
                pin, connector.ToString()), SensorUnit.Amperes, connector == 1 ? pin : 0, now));
        }

        var frame = new SensorSnapshot(1, 1, now, [.. sensors], [new PluginHealth("msi-psu", PluginState.Ready, now, null)], []);
        panel.Apply(frame, new AppPreferences { PinMetric = PinMetric.Voltage }, now);
        TestAssert.Equal<double?>(1, panel.Msi.Connectors[0].Pins[0].Current);
        TestAssert.Equal<double?>(0, panel.Msi.Connectors[1].Pins[0].Current);
        TestAssert.Equal("A", panel.Msi.Connectors[0].Pins[0].Unit);
        TestAssert.False(panel.Msi.Connectors[0].Pins[0].ShowHeadroom, "No unverified protection headroom should be displayed.");
        panel.MarkDisconnected(new AppPreferences());
        TestAssert.True(panel.Msi.Connectors.All(connector => connector.Pins.All(pin => pin.Current is null)), "Disconnect must clear both connectors.");
    }

    private static void EditsCurvePointsAndPreservesSavedSettings()
    {
        var original = new MsiOptions { FanControl = MsiFanControl.Curve };
        var editor = new MsiSettingsEditorViewModel(original);
        TestAssert.True(editor.Matches(original), "Loading a saved curve must not change it.");
        editor.AddCurvePoint();
        TestAssert.Equal(3, editor.CurvePoints.Count);
        TestAssert.False(editor.Matches(original), "Adding a point must mark the draft changed.");
        var saved = editor.Build();
        TestAssert.Equal(3, saved.FanCurve.Points.Length);
        TestAssert.True(original.FanCurve.Points.Length == 2, "Draft edits must not mutate service configuration.");
        editor.CurvePoints[1].DutyPercent = 50.5;
        editor.CurvePoints[1].TemperatureC = 52.5;
        TestAssert.Equal(51, editor.Build().FanCurve.Points[1].DutyPercent);
        TestAssert.Equal(53d, editor.Build().FanCurve.Points[1].TemperatureC);
        editor.RemoveCurvePoint(editor.CurvePoints[1]);
        TestAssert.True(editor.Matches(original), "Removing the added point must restore the original draft.");
        editor.ResponseDelaySeconds = 10;
        TestAssert.Equal(10, editor.Build().FanCurve.ResponseDelaySeconds);
        editor.CurvePoints[0].TemperatureC = double.NaN;
        TestAssert.Throws<ArgumentException>(() => editor.Build());
    }

    private static void SnapsLowDutyToAutomatic()
    {
        var editor = new MsiSettingsEditorViewModel(new MsiOptions());
        TestAssert.Equal(2d, editor.ResponseDelaySeconds);
        editor.CurvePoints[0].DutyPercent = 24;
        TestAssert.Equal(0d, editor.CurvePoints[0].DutyPercent);
        TestAssert.Equal(0, editor.Build().FanCurve.Points[0].DutyPercent);
        editor.CurvePoints[0].DutyPercent = 25;
        TestAssert.Equal(25, editor.Build().FanCurve.Points[0].DutyPercent);
        editor.AddCurvePoint(10, 15);
        TestAssert.Equal(0d, editor.CurvePoints[0].DutyPercent);
    }

    private static void LimitsCurvePointsAndAllowsFallingDuty()
    {
        var editor = new MsiSettingsEditorViewModel(new MsiOptions());
        editor.CurvePoints[0].TemperatureC = 35;
        editor.CurvePoints[0].DutyPercent = 50;
        editor.CurvePoints[1].TemperatureC = 40;
        editor.CurvePoints[1].DutyPercent = 30;
        editor.Build();

        while (editor.CanAddCurvePoint)
        {
            editor.AddCurvePoint();
        }

        TestAssert.Equal(6, editor.CurvePoints.Count);
        editor.AddCurvePoint(99, 50);
        TestAssert.Equal(6, editor.CurvePoints.Count);
        editor.RemoveCurvePoint(editor.CurvePoints[2]);
        TestAssert.True(editor.CanAddCurvePoint, "Removing a point must enable Add point again.");
    }

    private static async Task SynchronizesSavedCurvesWithoutDiscardingOtherDraftsAsync()
    {
        var settings = new PluginSettings { Options = JsonSerializer.SerializeToElement(new MsiOptions(), ClientJsonContext.Default.MsiOptions) };
        var baseline = new ConfigurationSnapshot(1, new ServiceConfiguration
        {
            Plugins = new Dictionary<string, PluginSettings> { ["msi-psu"] = settings }
        }, Version: "test:1");
        var api = new FakeMonitoringApi { Configuration = baseline };
        var editor = new ConfigurationEditorViewModel(() => api);
        editor.Receive(baseline);
        var msi = editor.Plugins.Single();
        msi.Msi.FanModeIndex = (int)MsiFanControl.Curve;
        msi.PollIntervalMs = 2000;
        var curve = new MsiFanCurve { Points = [new(35, 50), new(40, 30)] };
        var options = new MsiOptions { FanControl = MsiFanControl.Curve, FanCurve = curve };
        var saved = await api.SetConfigurationAsync(baseline.Configuration with
        {
            Plugins = new Dictionary<string, PluginSettings>
            {
                ["msi-psu"] = settings with { Options = JsonSerializer.SerializeToElement(options, ClientJsonContext.Default.MsiOptions) }
            }
        }, baseline.Version!, CancellationToken.None);

        editor.ReceiveMsiCurveUpdate(saved, curve, baseline.Version!);
        TestAssert.Equal(2000d, msi.PollIntervalMs);
        TestAssert.True(editor.IsDirty, "Unrelated local drafts must remain unsaved.");
        msi.Msi.FanModeIndex = (int)MsiFanControl.Automatic;
        await editor.ApplyAsync(CancellationToken.None);
        var actual = api.Configuration.Configuration.Plugins["msi-psu"].Options!.Value.Deserialize(ClientJsonContext.Default.MsiOptions)!;
        TestAssert.Equal(MsiFanControl.Automatic, actual.FanControl);
        TestAssert.False(editor.HasExternalChanges, "The main Apply must use the updated version after a curve save.");
    }

    private static void TracksFanDraftsAndValidatesPolling()
    {
        var settings = new PluginSettings { Options = JsonSerializer.SerializeToElement(new MsiOptions(), ClientJsonContext.Default.MsiOptions) };
        var editor = new ConfigurationEditorViewModel(() => new FakeMonitoringApi());
        editor.Receive(new ConfigurationSnapshot(1, new ServiceConfiguration { Plugins = new Dictionary<string, PluginSettings> { ["msi-psu"] = settings } }));
        var plugin = editor.Plugins.Single();
        plugin.Msi.FanModeIndex = (int)MsiFanControl.Manual;
        plugin.Msi.FanDutyPercent = 75;
        TestAssert.True(editor.IsDirty && plugin.Msi.IsManual, "Manual fan control must be an editable draft.");
        var built = plugin.Build().Options!.Value.Deserialize(ClientJsonContext.Default.MsiOptions)!;
        TestAssert.Equal(75, built.FanDutyPercent);
        plugin.Msi.FanModeIndex = (int)MsiFanControl.Unmanaged;
        plugin.Msi.FanDutyPercent = 50;
        TestAssert.False(editor.IsDirty, "Reverting MSI options must hide the edit footer.");
        plugin.PollIntervalMs = 500;
        TestAssert.Throws<ArgumentException>(() => plugin.Build());
        plugin.PollIntervalMs = 1000;
        plugin.Msi.FanDutyPercent = double.NaN;
        TestAssert.Throws<ArgumentException>(() => plugin.Build());
    }
}
