using System.Runtime.CompilerServices;
using RTXLens.Client.Http;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Discovery;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Health;
using RTXLens.Contracts.Sensors;
using RTXLens.Debugging.Models;
using RTXLens.Plugins.Asus.Contracts.Common;
using RTXLens.Plugins.Msi.Contracts.Devices;
using RTXLens.Plugins.WireView.Contracts.Common;
using RTXLens.Plugins.WireView.Contracts.Input;

namespace RTXLens.Debugging.Api;

public sealed class DebugMonitoringApi(DebugSession session, TimeProvider clock) : IMonitoringApi
{
    public Task<Contracts.Updates.ServiceVersionResponse?> GetVersionAsync(CancellationToken token) =>
        Task.FromResult<Contracts.Updates.ServiceVersionResponse?>(new(1, "1.0.0"));

    public Uri Address { get; } = new("http://debug.invalid/");
    private DebugFrame Current => session.Compose(clock.GetUtcNow());

    public Task<PluginCatalog> GetPluginsAsync(CancellationToken token) => Task.FromResult(Current.Catalog);

    public Task<ConfigurationSnapshot> GetConfigurationAsync(CancellationToken token) => Task.FromResult(Current.Configuration);

    public Task<HealthResponse> GetHealthAsync(CancellationToken token) => Task.FromResult(new HealthResponse("DEBUG", clock.GetUtcNow(), 0, Current.Snapshot.Plugins, 0));

    public Task<ConfigurationSnapshot> SetConfigurationAsync(ServiceConfiguration configuration, string version, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        session.SetConfiguration(configuration, version);
        return Task.FromResult(Current.Configuration);
    }

    public Task<DeviceConfig> GetWireViewConfigurationAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var configuration = session.GetSimulatedWireViewConfiguration()
            ?? throw new IOException("The simulated WireView Pro II is not available.");

        // Refresh should reload the editor even when the saved values are unchanged.
        return Task.FromResult(configuration with { });
    }

    public Task<DeviceConfig> ApplyWireViewConfigurationAsync(DeviceConfig changes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(session.ApplySimulatedWireViewConfiguration(changes));
    }

    public Task<RTXLens.Plugins.GenericNvidia.Contracts.Devices.NvidiaDeviceInfo[]> GetNvidiaDevicesAsync(CancellationToken token) =>
        Task.FromResult<RTXLens.Plugins.GenericNvidia.Contracts.Devices.NvidiaDeviceInfo[]>([new(session.Plugins.Single(plugin => plugin.Id == "generic-nvidia").DeviceId, "DEBUG GeForce RTX GPU", "debug-nvidia")]);

    public Task<AsusDeviceInfo[]> GetAsusDevicesAsync(CancellationToken token) => Task.FromResult<AsusDeviceInfo[]>([new AsusDeviceInfo(session.Plugins[1].DeviceId, "DEBUG ROG Astral")]);
    public Task<MsiDeviceInfo[]> GetMsiDevicesAsync(CancellationToken token) => Task.FromResult<MsiDeviceInfo[]>([new MsiDeviceInfo(session.Plugins[2].DeviceId, "DEBUG MPG TS PSU")]);

    public Task ClearWireViewFaultsAsync(ClearFaultsRequest request, CancellationToken token)
    {
        var plugin = session.Plugins[0];
        foreach (var metric in plugin.Metrics.Where(metric => metric.Definition.Metric is SensorMetric.FaultStatus or SensorMetric.FaultLog))
        {
            var mask = metric.Definition.Metric == SensorMetric.FaultStatus ? request.FaultStatusMask : request.FaultLogMask;
            if (mask == 0)
            {
                continue;
            }

            metric.Value = (ushort)metric.Value & ~mask;
            metric.ModeIndex = (int)DebugMetricMode.Fixed;
        }
        return Task.CompletedTask;
    }

    public Task RestartPluginAsync(string pluginId, CancellationToken token)
    {
        session.RestartAlerts();
        return Task.CompletedTask;
    }

    public Task RestartServiceAsync(CancellationToken token)
    {
        session.RestartAlerts();
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<SensorSnapshot> ReadSensorsAsync([EnumeratorCancellation] CancellationToken token)
    {
        while (session.Enabled && !token.IsCancellationRequested)
        {
            yield return Current.Snapshot;
            await Task.Delay(TimeSpan.FromSeconds(1), clock, token);
        }
    }
}
