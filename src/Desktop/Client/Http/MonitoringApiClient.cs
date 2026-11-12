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
        using var response = await http.GetAsync("version", token);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, token);
        var value = await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.ServiceVersionResponse, token);

        return value is { SchemaVersion: 1 }
            && Contracts.Updates.SemanticVersion.TryParse(value.Version, out _) ? value : null;
    }

    public Task<HealthResponse> GetHealthAsync(CancellationToken token) => GetAsync("health", ClientJsonContext.Default.HealthResponse, token);
    public Task<PluginCatalog> GetPluginsAsync(CancellationToken token) => GetAsync("plugins", ClientJsonContext.Default.PluginCatalog, token);
    public Task<SettingsProfileCatalog> GetSettingsProfilesAsync(CancellationToken token) =>
        GetAsync("settings-profiles", ClientJsonContext.Default.SettingsProfileCatalog, token);

    public async Task<SettingsProfileCatalog> ApplySettingsProfileAsync(SettingsProfileRequest request, CancellationToken token)
    {
        using var response = await http.PutAsJsonAsync("settings-profiles", request, ClientJsonContext.Default.SettingsProfileRequest, token);
        return await ReadSettingsProfilesAsync(response, token);
    }

    public async Task<SettingsProfileCatalog> UpdateSettingsProfileAsync(SettingsProfile profile, CancellationToken token)
    {
        using var response = await http.PutAsJsonAsync($"settings-profiles/{Uri.EscapeDataString(profile.Id)}", profile,
            ClientJsonContext.Default.SettingsProfile, token);
        return await ReadSettingsProfilesAsync(response, token);
    }

    public async Task<SettingsProfileCatalog> DeleteSettingsProfileAsync(string id, CancellationToken token)
    {
        using var response = await http.DeleteAsync($"settings-profiles/{Uri.EscapeDataString(id)}", token);
        return await ReadSettingsProfilesAsync(response, token);
    }

    private static async Task<SettingsProfileCatalog> ReadSettingsProfilesAsync(HttpResponseMessage response, CancellationToken token)
    {
        await EnsureSuccessAsync(response, token);
        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.SettingsProfileCatalog, token)
            ?? throw new InvalidDataException("The service returned an empty GPU settings profile response.");
    }

    public Task<DeviceWarningProfileCatalog> GetDeviceWarningProfilesAsync(CancellationToken token) =>
        GetAsync("warning-profiles", ClientJsonContext.Default.DeviceWarningProfileCatalog, token);

    public async Task SaveDeviceWarningProfileAsync(DeviceWarningProfile profile, CancellationToken token)
    {
        using var response = await http.PutAsJsonAsync("warning-profiles", profile, ClientJsonContext.Default.DeviceWarningProfile, token);
        await EnsureSuccessAsync(response, token);
    }

    public Task DeleteDeviceWarningProfileAsync(string id, CancellationToken token) =>
        DeleteAsync($"warning-profiles/{Uri.EscapeDataString(id)}", token);

    public async Task<WarningShutdownReason?> GetWarningShutdownAsync(CancellationToken token)
    {
        using var response = await http.GetAsync("warning-shutdown", token);
        if (response.StatusCode is System.Net.HttpStatusCode.NoContent or System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, token);
        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.WarningShutdownReason, token);
    }

    public Task AcknowledgeWarningShutdownAsync(string id, CancellationToken token) =>
        DeleteAsync($"warning-shutdown/{Uri.EscapeDataString(id)}", token);

    private async Task DeleteAsync(string path, CancellationToken token)
    {
        using var response = await http.DeleteAsync(path, token);
        await EnsureSuccessAsync(response, token);
    }

    public async Task<ConfigurationSnapshot> GetConfigurationAsync(CancellationToken token) =>
        NormalizeConfiguration(await GetAsync("configuration", ClientJsonContext.Default.ConfigurationSnapshot, token));
    public Task<DeviceConfig> GetWireViewConfigurationAsync(CancellationToken token) =>
        GetAsync("plugins/wireview-pro2/device/config", ClientJsonContext.Default.DeviceConfig, token);
    public async Task<DeviceConfig> ApplyWireViewConfigurationAsync(DeviceConfig changes, CancellationToken token)
    {
        using var response = await http.PutAsJsonAsync("plugins/wireview-pro2/device/config", changes,
            ClientJsonContext.Default.DeviceConfig, token);

        await EnsureSuccessAsync(response, token);
        return await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.DeviceConfig, token)
            ?? throw new InvalidDataException("The service returned empty WireView settings.");
    }

    public Task<AsusDeviceInfo[]> GetAsusDevicesAsync(CancellationToken token) =>
        GetAsync("plugins/asus-astral/devices", ClientJsonContext.Default.AsusDeviceInfoArray, token);
    public Task<NvidiaDeviceInfo[]> GetNvidiaDevicesAsync(CancellationToken token) => GetNvidiaDevicesAsync(true, token);

    public Task<NvidiaDeviceInfo[]> GetNvidiaDevicesAsync(bool refresh, CancellationToken token) =>
        GetAsync(refresh ? "plugins/generic-nvidia/devices" : "plugins/generic-nvidia/devices?refresh=false",
            ClientJsonContext.Default.NvidiaDeviceInfoArray, token);

    public Task<MsiDeviceInfo[]> GetMsiDevicesAsync(CancellationToken token) =>
        GetAsync("plugins/msi-psu/devices", ClientJsonContext.Default.MsiDeviceInfoArray, token);

    public Task<RTXLens.Plugins.Msi.Contracts.Telemetry.MsiSnapshot> GetMsiSnapshotAsync(CancellationToken token) =>
        GetAsync("plugins/msi-psu/device", ClientJsonContext.Default.MsiSnapshot, token);

    public async Task<ConfigurationSnapshot> SetConfigurationAsync(ServiceConfiguration configuration, string version, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new ArgumentException("Reload configuration before applying changes: its version is missing.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, "configuration")
        {
            Content = JsonContent.Create(configuration, ClientJsonContext.Default.ServiceConfiguration)
        };
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
        using var response = await http.PostAsJsonAsync("plugins/wireview-pro2/faults/clear", request, ClientJsonContext.Default.ClearFaultsRequest, token);
        await EnsureSuccessAsync(response, token);
    }

    public Task RestartPluginAsync(string pluginId, CancellationToken token) => PostAsync($"plugins/{Uri.EscapeDataString(pluginId)}/restart", token);
    public Task RestartServiceAsync(CancellationToken token) => PostAsync("restart", token);

    public async IAsyncEnumerable<SensorSnapshot> ReadSensorsAsync([EnumeratorCancellation] CancellationToken token)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        var address = new UriBuilder(new Uri(Address, "sensors")) { Scheme = Address.Scheme == "https" ? "wss" : "ws" }.Uri;
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
            // Older servers/proxies may return an HTML error page.
        }

        throw new MonitoringApiException(response.StatusCode, message);
    }
}
