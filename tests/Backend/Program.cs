using RTXLens.Tests.Core;
using RTXLens.Tests.Infrastructure;
using RTXLens.Tests.Service;

var runner = new TestRunner();
if (args.Contains("--verify-msi-hardware"))
{
    await MsiHardwareVerification.RunAsync();
    return 0;
}

if (args.Contains("--verify-msi-curve-hardware"))
{
    await MsiCurveHardwareVerification.RunAsync();
    return 0;
}

GenericNvidiaTests.Run(runner);
ConnectorProfileTests.Run(runner);
SettingsProfileTests.Run(runner);
await DeviceWarningProfileTests.RunAsync(runner);
HardwareDiscoveryTests.Run(runner);
WarningEngineTests.Run(runner);
WarningRegistryTests.Run(runner);
SourceSelectionTests.Run(runner);
SensorStateStoreTests.Run(runner);
SensorAlertTests.Run(runner);
MonitoringOptionsTests.Run(runner);
SensorIdentityTests.Run(runner);
SensorStreamHubTests.Run(runner);
WireViewConfigParserTests.Run(runner);
AsusAlertTests.Run(runner);
await AsusPluginTests.RunAsync(runner);
await MsiPluginTests.RunAsync(runner);
MsiFanCurveTests.Run(runner);
await MsiEndpointTests.RunAsync(runner);

SafeguardEmailTests.Run(runner);
await SettingsProfileTests.RunEndpointsAsync(runner);
await WarningConfigurationTests.RunAsync(runner);
await HealthEndpointTests.RunAsync(runner);
await ServiceLogTests.RunAsync(runner);
await TestHostConfigurationTests.RunAsync(runner);
await SensorEndpointTests.RunAsync(runner);
await RuntimeConfigurationTests.RunAsync(runner);
await WireViewEndpointTests.RunAsync(runner);
await AsusEndpointTests.RunAsync(runner);
await DesktopClientTests.RunAsync(runner);

runner.PrintSummary();
return runner.FailedCount == 0 ? 0 : 1;
