using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Health;
using RTXLens.Contracts.Monitoring;
using RTXLens.Contracts.Sensors;
using RTXLens.Presentation.Configuration;
using RTXLens.Presentation.Settings;
using RTXLens.Presentation.Telemetry;
using RTXLens.Tests.Infrastructure;

namespace RTXLens.UI.Tests.Configuration;

internal static class ConnectorProfilesTests
{
    public static void Run(TestRunner runner)
    {
        runner.Run(EditorsPreserveStableIdsAndTrackRevertedPriorities);
        runner.Run(GpuStatusUsesResolvedSourcesInsteadOfItsRawPluginReadings);
    }

    private static ConnectorProfiles Profiles() => new()
    {
        Version = 1,
        Profiles = [new ConnectorSourceProfile
        {
            Id = "automatic", Name = "Automatic",
            Metrics = [.. new[] { SensorMetric.Power, SensorMetric.Current, SensorMetric.Voltage }.Select(metric => new ConnectorMetricSelection
            {
                Metric = metric, PreferredPlugins = ["generic-nvidia", "wireview-pro2", "msi-psu"]
            })]
        }]
    };

    private static void EditorsPreserveStableIdsAndTrackRevertedPriorities()
    {
        var baseline = Profiles();
        var model = new ConnectorProfilesEditorViewModel();
        model.Load(baseline, null);
        TestAssert.True(model.Matches(baseline), "Loading must not edit profiles.");
        var metric = model.SelectedProfile!.Metrics[0];
        metric.Sources.Move(0, 1);
        TestAssert.False(model.Matches(baseline), "Changing source order must dirty the draft.");
        metric.Sources.Move(1, 0);
        TestAssert.True(model.Matches(baseline), "Restoring source order must restore the baseline.");
        model.SelectedProfile.MsiConnectorIndex = 2;
        TestAssert.Equal("2", model.Build().Profiles[0].Connectors["msi-psu"]);
        TestAssert.Equal("automatic", model.Build().ActiveProfileId);
    }

    private static void GpuStatusUsesResolvedSourcesInsteadOfItsRawPluginReadings()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new SensorReading(new SensorIdentity("generic-nvidia", "gpu0", SensorComponent.GpuCore, SensorMetric.Clock), SensorUnit.Megahertz, 1500, now);
        var power = new SensorReading(new SensorIdentity("wireview-pro2", "wire0", SensorComponent.GpuConnector, SensorMetric.Power, 1), SensorUnit.Watts, 300, now);
        var current = new SensorReading(new SensorIdentity("msi-psu", "psu0", SensorComponent.PsuConnector, SensorMetric.Current, ConnectorId: "2"), SensorUnit.Amperes, 25, now);
        var voltage = power with { Identity = power.Identity with { Metric = SensorMetric.Voltage }, Unit = SensorUnit.Volts, Value = 12 };
        var readings = new[] { clock, power, current, voltage };
        var snapshot = new SensorSnapshot(1, 1, now, readings,
            [.. new[] { "generic-nvidia", "wireview-pro2", "msi-psu" }.Select(id => new PluginHealth(id, PluginState.Ready, now, null))],
            [
                new ResolvedSelection(ConnectorProfiles.SelectionName(SensorMetric.Power), SelectionState.Selected, power.Identity.Key),
                new ResolvedSelection(ConnectorProfiles.SelectionName(SensorMetric.Current), SelectionState.Fallback, current.Identity.Key),
                new ResolvedSelection(ConnectorProfiles.SelectionName(SensorMetric.Voltage), SelectionState.Fallback, voltage.Identity.Key)
            ]);
        var configuration = new ConfigurationSnapshot(1, new ServiceConfiguration
        {
            Plugins = new[] { "generic-nvidia", "wireview-pro2", "msi-psu" }.ToDictionary(id => id, _ => new PluginSettings()),
            Monitoring = new MonitoringOptions { ConnectorProfiles = Profiles() }
        });
        var model = new GpuStatusViewModel();
        model.Apply(snapshot, configuration, new AppPreferences(), now);
        TestAssert.True(model.PowerText.Contains("300"), "WireView power must be selected by the service profile.");
        TestAssert.True(model.CurrentText.Contains("25"), "MSI connector current must be selected across connector scope.");
        TestAssert.True(model.VoltageText.Contains("12"), "Resolved voltage must replace native plugin voltage.");
        model.Apply(snapshot with { Selections = [] }, configuration, new AppPreferences(), now);
        TestAssert.Equal("Unavailable", model.PowerText);

        var editor = new ConnectorProfilesEditorViewModel();
        editor.Load(configuration.Configuration.Monitoring.ConnectorProfiles, null);
        editor.Apply(snapshot);
        var metrics = editor.SelectedProfile!.Metrics.ToDictionary(metric => metric.Metric);
        TestAssert.Equal("Using WireView Pro II", metrics[SensorMetric.Power].Resolution);
        TestAssert.Equal("Using MSI PSU · Connector 2 (fallback)", metrics[SensorMetric.Current].Resolution);
        editor.Apply(snapshot with { Selections = [] });
        TestAssert.Equal("Unavailable - no source is reporting a usable reading", metrics[SensorMetric.Voltage].Resolution);
    }
}
