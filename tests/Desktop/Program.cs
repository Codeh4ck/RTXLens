using RTXLens.Tests.Infrastructure;
using RTXLens.UI.Tests.Configuration;
using RTXLens.UI.Tests.Settings;
using RTXLens.UI.Tests.Streaming;
using RTXLens.UI.Tests.Telemetry;

if (args is ["--verify-avalonia-controls"] or ["--verify-avalonia-controls", _])
{
    return RTXLens.UI.Tests.Controls.AvaloniaControlsTests.Run(args.Length == 2 ? args[1] : null);
}

if (args is ["--verify-release", var directory])
{
    RTXLens.UI.Tests.BuildVerification.ReleaseBuildVerification.Run(directory);
    return 0;
}

if (args is ["--performance-fixture", var output])
{
    File.WriteAllText(output, RTXLens.Presentation.Performance.PerformanceHtmlExporter.Export(
        RTXLens.UI.Tests.Performance.PerformanceRecordingTests.Fixture()));
    return 0;
}

var runner = new TestRunner();
RTXLens.UI.Tests.Performance.PerformanceRecordingTests.Run(runner);
TelemetryTests.Run(runner);
HardwarePanelTests.Run(runner);
WireViewPanelTests.Run(runner);
WarningDeliveryTests.Run(runner);
MsiPanelTests.Run(runner);
PsuComparisonTests.Run(runner);
DirtyTrackingTests.Run(runner);
DeviceRefreshTests.Run(runner);
GenericNvidiaEditorTests.Run(runner);
AfterburnerConfigurationTests.Run(runner);
ConnectorProfilesTests.Run(runner);
await EditorTests.RunAsync(runner);
await LegacyConfigurationTests.RunAsync(runner);
await PreferenceTests.RunAsync(runner);
await RTXLens.UI.Tests.Updates.UpdateTests.RunAsync(runner);
await SessionTests.RunAsync(runner);
await FrameReaderTests.RunAsync(runner);
#if TELEMETRY_DEBUG
await RTXLens.UI.Tests.Debugging.DebugSessionTests.RunAsync(runner);
#endif
runner.PrintSummary();
return runner.FailedCount == 0 ? 0 : 1;
