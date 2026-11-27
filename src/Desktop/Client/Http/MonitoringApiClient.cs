using RTXLens.Plugins.GenericNvidia.Contracts;
using RTXLens.Plugins.Msi.Contracts;
using RTXLens.Plugins.Asus.Contracts;
using RTXLens.Contracts.Monitoring;
using RTXLens.Plugins.WireView.Contracts;
using RTXLens.Plugins.GenericNvidia.Contracts.Devices;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RTXLens.Client.Serialization;
using RTXLens.Client.Streaming;
using RTXLens.Plugins.Asus.Contracts.Common;
using RTXLens.Plugins.Msi.Contracts.Devices;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Discovery;
using RTXLens.Contracts.Health;
using RTXLens.Contracts.Sensors;
using RTXLens.Plugins.WireView.Contracts.Common;
using RTXLens.Plugins.WireView.Contracts.Input;

namespace RTXLens.Client.Http;

public sealed class MonitoringApiClient(HttpClient http) : IMonitoringApi
{
    public Uri Address => http.BaseAddress ?? throw new InvalidOperationException("Service base address is required.");

    public async Task<RTXLens.Contracts.Updates.ServiceVersionResponse?> GetVersionAsync(CancellationToken token)
    {
        using var response = await http.GetAsync(ServiceRoutes.Version, token);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, token);
        var value = await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.ServiceVersionResponse, token);

        return value is { SchemaVersion: 1 }
            && Contracts.Updates.SemanticVersion.TryParse(value.Version, out _) ? value : null;
    }

    public Task<Contracts.Warnings.WarningIncident[]> GetWarningHistoryAsync(CancellationToken token) =>
        GetAsync(ServiceRoutes.WarningHistory, ClientJsonContext.Default.WarningIncidentArray, token);

    public async Task<Contracts.Warnings.WarningIncident[]> ClearWarningHistoryAsync(CancellationToken token)
    {
        using var response = await http.DeleteAsync(ServiceRoutes.WarningHistory, token);

        await EnsureSuccessAsync(response, token);

        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.WarningIncidentArray, token)
            ?? throw new InvalidDataException("The service returned an empty warning history response.");
    }

    public Task<Contracts.Warnings.WarningIncident> GetWarningIncidentAsync(string kind, Guid id, CancellationToken token) =>
        GetAsync($"{ServiceRoutes.WarningHistory}/{Uri.EscapeDataString(kind)}/{id:D}", ClientJsonContext.Default.WarningIncident, token);

    public Task<HealthResponse> GetHealthAsync(CancellationToken token) => GetAsync(ServiceRoutes.Health, ClientJsonContext.Default.HealthResponse, token);
    public Task<PluginCatalog> GetPluginsAsync(CancellationToken token) => GetAsync(ServiceRoutes.Plugins, ClientJsonContext.Default.PluginCatalog, token);

    public Task<SettingsProfileCatalog> GetSettingsProfilesAsync(CancellationToken token) =>
        GetAsync(ServiceRoutes.SettingsProfiles, ClientJsonContext.Default.SettingsProfileCatalog, token);

    public async Task<SettingsProfileCatalog> ApplySettingsProfileAsync(SettingsProfileRequest request, CancellationToken token)
    {
        using var response = await http.PutAsJsonAsync(ServiceRoutes.SettingsProfiles, request, ClientJsonContext.Default.SettingsProfileRequest, token);
        return await ReadSettingsProfilesAsync(response, token);
    }

    public async Task<SettingsProfileCatalog> UpdateSettingsProfileAsync(SettingsProfile profile, CancellationToken token)
    {
        using var response = await http.PutAsJsonAsync($"{ServiceRoutes.SettingsProfiles}/{Uri.EscapeDataString(profile.Id)}",
            profile, ClientJsonContext.Default.SettingsProfile, token);

        return await ReadSettingsProfilesAsync(response, token);
    }

    public async Task<SettingsProfileCatalog> DeleteSettingsProfileAsync(string id, CancellationToken token)
    {
        using var response = await http.DeleteAsync($"{ServiceRoutes.SettingsProfiles}/{Uri.EscapeDataString(id)}", token);
        return await ReadSettingsProfilesAsync(response, token);
    }

    private static async Task<SettingsProfileCatalog> ReadSettingsProfilesAsync(HttpResponseMessage response, CancellationToken token)
    {
        await EnsureSuccessAsync(response, token);

        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.SettingsProfileCatalog, token)
            ?? throw new InvalidDataException("The service returned an empty GPU settings profile response.");
    }

    public Task<DeviceWarningProfileCatalog> GetDeviceWarningProfilesAsync(CancellationToken token) =>
        GetAsync(ServiceRoutes.WarningProfiles, ClientJsonContext.Default.DeviceWarningProfileCatalog, token);

    public async Task PublishDebugPowerFrameAsync(DebugPowerFrame frame, CancellationToken token)
    {
        using var response = await http.PostAsJsonAsync($"{ServiceRoutes.WarningProfiles}/debug-power",
            frame, ClientJsonContext.Default.DebugPowerFrame, token);

        await EnsureSuccessAsync(response, token);
    }

    public Task RestoreGpuPowerAsync(string gpuId, CancellationToken token) =>
        PostPowerActionAsync($"{ServiceRoutes.WarningProfiles}/restore-power?gpuId={Uri.EscapeDataString(gpuId)}", token);

    private async Task PostPowerActionAsync(string path, CancellationToken token)
    {
        using var response = await http.PostAsync(path, null, token);
        await EnsureSuccessAsync(response, token);
    }

    public async Task SaveDeviceWarningProfileAsync(DeviceWarningProfile profile, CancellationToken token)
    {
        using var response = await http.PutAsJsonAsync(ServiceRoutes.WarningProfiles, profile, ClientJsonContext.Default.DeviceWarningProfile, token);
        await EnsureSuccessAsync(response, token);
    }

    public Task DeleteDeviceWarningProfileAsync(string id, CancellationToken token) =>
        DeleteAsync($"{ServiceRoutes.WarningProfiles}/{Uri.EscapeDataString(id)}", token);

    public async Task<WarningShutdownReason?> GetWarningShutdownAsync(CancellationToken token)
    {
        using var response = await http.GetAsync(ServiceRoutes.WarningShutdown, token);
        if (response.StatusCode is System.Net.HttpStatusCode.NoContent or System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, token);
        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.WarningShutdownReason, token);
    }

    public Task AcknowledgeWarningShutdownAsync(string id, CancellationToken token) =>
        DeleteAsync($"{ServiceRoutes.WarningShutdown}/{Uri.EscapeDataString(id)}", token);

    private async Task DeleteAsync(string path, CancellationToken token)
    {
        using var response = await http.DeleteAsync(path, token);
        await EnsureSuccessAsync(response, token);
    }

    public async Task<ConfigurationSnapshot> GetConfigurationAsync(CancellationToken token) =>
        NormalizeConfiguration(await GetAsync(ServiceRoutes.Configuration, ClientJsonContext.Default.ConfigurationSnapshot, token));

    public Task<DeviceConfig> GetWireViewConfigurationAsync(CancellationToken token) =>
        GetAsync(WireViewIdentity.Route + WireViewIdentity.DeviceConfigPath, ClientJsonContext.Default.DeviceConfig, token);

    public async Task<DeviceConfig> ApplyWireViewConfigurationAsync(DeviceConfig changes, CancellationToken token)
    {
        using var response = await http.PutAsJsonAsync(WireViewIdentity.Route + WireViewIdentity.DeviceConfigPath, changes,
            ClientJsonContext.Default.DeviceConfig, token);

        await EnsureSuccessAsync(response, token);

        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.DeviceConfig, token)
            ?? throw new InvalidDataException("The service returned empty WireView settings.");
    }

    public Task<AsusDeviceInfo[]> GetAsusDevicesAsync(CancellationToken token) =>
        GetAsync(AsusIdentity.Route + "/devices", ClientJsonContext.Default.AsusDeviceInfoArray, token);

    public Task<NvidiaDeviceInfo[]> GetNvidiaDevicesAsync(CancellationToken token) => GetNvidiaDevicesAsync(true, token);

    public Task<NvidiaDeviceInfo[]> GetNvidiaDevicesAsync(bool refresh, CancellationToken token) =>
        GetAsync(refresh ? NvidiaIdentity.Route + "/devices" : NvidiaIdentity.Route + "/devices?refresh=false",
            ClientJsonContext.Default.NvidiaDeviceInfoArray, token);

    public Task<MsiDeviceInfo[]> GetMsiDevicesAsync(CancellationToken token) =>
        GetAsync(MsiIdentity.Route + "/devices", ClientJsonContext.Default.MsiDeviceInfoArray, token);

    public Task<RTXLens.Plugins.Msi.Contracts.Telemetry.MsiSnapshot> GetMsiSnapshotAsync(CancellationToken token) =>
        GetAsync(MsiIdentity.Route + "/device", ClientJsonContext.Default.MsiSnapshot, token);

    public async Task<ConfigurationSnapshot> SetConfigurationAsync(ServiceConfiguration configuration, string version, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new ArgumentException("Reload configuration before applying changes: its version is missing.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, "configuration");

        request.Content = JsonContent.Create(configuration, ClientJsonContext.Default.ServiceConfiguration);
        request.Headers.IfMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue($"\"{version}\""));

        using var response = await http.SendAsync(request, token);
        await EnsureSuccessAsync(response, token);

        return NormalizeConfiguration(await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.ConfigurationSnapshot, token)
                                      ?? throw new InvalidDataException("The service returned empty configuration."));
    }

    private static ConfigurationSnapshot NormalizeConfiguration(ConfigurationSnapshot snapshot)
    {
        // Older services omit warnings. Source-generated record deserialization can assign null
        // to an absent init property instead of retaining its initializer.
        var warnings = snapshot.Configuration.Warnings ?? new Contracts.Warnings.WarningOptions();

        if (!warnings.IsValid())
        {
            throw new InvalidDataException("The service returned invalid warning settings.");
        }

        return snapshot with { Configuration = snapshot.Configuration with { Warnings = warnings } };
    }

    public async Task ClearWireViewFaultsAsync(ClearFaultsRequest request, CancellationToken token)
    {
        using var response = await http.PostAsJsonAsync(WireViewIdentity.Route + WireViewIdentity.ClearFaultsPath, request, ClientJsonContext.Default.ClearFaultsRequest, token);
        await EnsureSuccessAsync(response, token);
    }

    public Task RestartPluginAsync(string pluginId, CancellationToken token) => PostAsync($"{ServiceRoutes.Plugins}/{Uri.EscapeDataString(pluginId)}/restart", token);
    public Task RestartServiceAsync(CancellationToken token) => PostAsync(ServiceRoutes.Restart, token);

    public async IAsyncEnumerable<SensorSnapshot> ReadSensorsAsync([EnumeratorCancellation] CancellationToken token)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

        var address = new UriBuilder(new Uri(Address, ServiceRoutes.Sensors)) { Scheme = Address.Scheme == "https" ? "wss" : "ws" }.Uri;
        await socket.ConnectAsync(address, token);

        await foreach (var frame in SensorFrameReader.ReadAsync(socket, token))
        {
            yield return frame;
        }
    }

    private async Task<T> GetAsync<T>(string path, JsonTypeInfo<T> type, CancellationToken token)
    {
        using var response = await http.GetAsync(path, token);
        await EnsureSuccessAsync(response, token);

        return await response.Content.ReadFromJsonAsync(type, token) ?? throw new InvalidDataException("The service returned an empty response.");
    }

    private async Task PostAsync(string path, CancellationToken token)
    {
        using var response = await http.PostAsync(path, null, token);
        await EnsureSuccessAsync(response, token);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var message = $"Service request failed: {(int)response.StatusCode} {response.ReasonPhrase}.";
        try
        {
            var error = await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.ErrorResponse, token);
            if (!string.IsNullOrWhiteSpace(error?.Error))
            {
                message = error.Error;
            }
        }
        catch (JsonException)
        {
        }

        throw new MonitoringApiException(response.StatusCode, message);
    }
}
