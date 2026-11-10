using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RTXLens.Core.Plugins;
using RTXLens.Hardware.Nvidia;
using RTXLens.Plugins.GenericNvidia;
using RTXLens.Contracts.Configuration;
using RTXLens.Plugins.WireView;
using RTXLens.Plugins.WireView.Endpoints;
using RTXLens.Plugins.WireView.Streaming;
using RTXLens.Service;
using RTXLens.Service.Configuration;
using RTXLens.Service.Services;
using RTXLens.Service.Endpoints;
using RTXLens.Tests.TestDoubles;
using RTXLens.Plugins.Asus.Endpoints;
using RTXLens.Plugins.Asus;
using RTXLens.Plugins.Asus.Serialization;
using RTXLens.Plugins.Asus.Streaming;
using RTXLens.Plugins.Msi;
using RTXLens.Plugins.Msi.Endpoints;
using RTXLens.Plugins.Msi.Serialization;

namespace RTXLens.Tests.Infrastructure;

internal sealed class MonitoringTestHost : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly Uri _sensorStreamAddress;

    public HttpClient Client { get; }

    public ILogger CreateLogger(string category) => _application.Services.GetRequiredService<ILoggerFactory>().CreateLogger(category);

    public string ConfigurationPath { get; }
    public int RestartRequests => ((TestServiceRestart)_application.Services.GetRequiredService<IServiceRestart>()).RequestCount;

    private MonitoringTestHost(WebApplication application, string address, string configurationPath)
    {
        _application = application;
        ConfigurationPath = configurationPath;
        Client = new HttpClient
        {
            BaseAddress = new Uri(address),
            Timeout = TimeSpan.FromSeconds(5)
        };

        _sensorStreamAddress = new UriBuilder(address)
        {
            Scheme = "ws",
            Path = "/sensors"
        }.Uri;
    }

    public static Task<MonitoringTestHost> StartWithTestPluginsAsync() => StartAsync(registerTestPlugins: true);
    public static Task<MonitoringTestHost> StartWithoutPluginsAsync() => StartAsync(registerTestPlugins: false);
    public static Task<MonitoringTestHost> StartWithMsiAsync(MsiTestState state) => StartAsync(false, msi: state);
    public static Task<MonitoringTestHost> StartWithMsiHardwareAsync() => StartAsync(false, useMsiHardware: true);
    public static Task<MonitoringTestHost> StartWithInstalledPluginsDisabledAsync(bool useShippedDefaults = false) => StartAsync(registerTestPlugins: false,
        useInstalledFactories: true,
        bootstrapFile: useShippedDefaults ? Path.Combine(AppContext.BaseDirectory, "Fixtures", "service-appsettings.json") : null,
        bootstrapOverrides: new Dictionary<string, string?>
        {
            ["Plugins:wireview-pro2:Enabled"] = "false",
            ["Plugins:asus-astral:Enabled"] = "false",
            ["Plugins:generic-nvidia:Enabled"] = "false",
            ["Plugins:msi-psu:Enabled"] = "false"
        });

    public static Task<MonitoringTestHost> StartWithWireViewAsync(WireViewTestState device, int initialPollMs = 1000) =>
        StartAsync(registerTestPlugins: false, device, initialPollMs);

    public static Task<MonitoringTestHost> StartWithAsusAsync(AsusTestState device, WireViewTestState? wireView = null,
        ServiceConfiguration? existingConfiguration = null, Dictionary<string, string?>? bootstrapOverrides = null) =>
        StartAsync(registerTestPlugins: false, wireView, asus: device, existingConfiguration: existingConfiguration,
            bootstrapOverrides: bootstrapOverrides);

    public async Task<ConfigurationSnapshot> GetConfigurationAsync()
    {
        using var document = JsonDocument.Parse(await Client.GetStringAsync("/configuration"));
        var root = document.RootElement;
        var configuration = RuntimeJson.Deserialize(root.GetProperty("configuration").GetRawText());
        return new ConfigurationSnapshot(root.GetProperty("revision").GetInt64(), configuration, root.GetProperty("lastError").GetString());
    }

    public async Task<long> SetPluginSettingsAsync(string pluginId, PluginSettings settings)
    {
        using var response = await Client.PutAsJsonAsync($"/plugins/{pluginId}/settings", settings);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("revision").GetInt64();
    }

    public async Task SetConfigurationAsync(ServiceConfiguration configuration)
    {
        using var contents = new StringContent(RuntimeJson.Serialize(configuration), System.Text.Encoding.UTF8, "application/json");
        using var response = await Client.PutAsync("/configuration", contents);
        response.EnsureSuccessStatusCode();
    }

    public async Task<JsonElement> GetHealthAsync()
    {
        var response = await Client.GetStringAsync("/health");
        using var document = JsonDocument.Parse(response);

        return document.RootElement.Clone();
    }

    public async Task<ClientWebSocket> ConnectSensorClientAsync(CancellationToken cancellationToken, string path = "/sensors")
    {
        var client = new ClientWebSocket();

        try
        {
            var address = new UriBuilder(_sensorStreamAddress) { Path = path }.Uri;
            await client.ConnectAsync(address, cancellationToken);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task<JsonElement> GetSensorSnapshotAsync(bool requireReadings = true)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var client = await ConnectSensorClientAsync(timeout.Token);

        while (true)
        {
            using var document = await WebSocketMessageReader.ReadJsonAsync(client, timeout.Token);
            var hasReadings = document.RootElement.GetProperty("sensors").GetArrayLength() > 0;

            if (requireReadings && !hasReadings)
            {
                continue;
            }

            var snapshot = document.RootElement.Clone();
            await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "Snapshot received", timeout.Token);

            return snapshot;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        try
        {
            await _application.StopAsync(timeout.Token);
        }
        finally
        {
            await _application.DisposeAsync();
            Directory.Delete(Path.GetDirectoryName(ConfigurationPath)!, recursive: true);
        }
    }

    private static async Task<MonitoringTestHost> StartAsync(bool registerTestPlugins, WireViewTestState? device = null,
        int initialPollMs = 1000, AsusTestState? asus = null, ServiceConfiguration? existingConfiguration = null,
        Dictionary<string, string?>? bootstrapOverrides = null, bool useInstalledFactories = false, MsiTestState? msi = null,
        bool useMsiHardware = false, string? bootstrapFile = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        // Test hosts use only explicit settings and requested fixtures, never inherited
        // JSON files or environment variables.
        builder.Configuration.Sources.Clear();

        if (bootstrapFile is not null)
        {
            builder.Configuration.AddJsonFile(bootstrapFile, optional: false, reloadOnChange: false);
        }

        builder.Logging.ClearProviders();
        var configurationPath = Path.Combine(AppContext.BaseDirectory, "TestData", Guid.NewGuid().ToString("N"), "appsettings.json");

        var settings = new Dictionary<string, string?>
        {
            ["RuntimeConfiguration:FilePath"] = configurationPath,
            ["SettingsProfiles:Directory"] = Path.Combine(Path.GetDirectoryName(configurationPath)!, "profiles")
        };

        if (bootstrapFile is null)
        {
            settings["Monitoring:StreamIntervalMs"] = "100";
        }

        if (device is not null)
        {
            settings["Plugins:wireview-pro2:Enabled"] = "true";
            settings["Plugins:wireview-pro2:PollIntervalMs"] = initialPollMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
            settings["Plugins:wireview-pro2:StaleAfterMs"] = Math.Max(5000, initialPollMs).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (bootstrapOverrides is not null)
        {
            foreach (var (key, value) in bootstrapOverrides)
            {
                settings[key] = value;
            }
        }

        if (existingConfiguration is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configurationPath)!);
            await File.WriteAllTextAsync(configurationPath, RuntimeJson.Serialize(existingConfiguration));
        }

        builder.Configuration.AddInMemoryCollection(settings);

        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));

        builder.Services.AddMonitoringCore(builder.Configuration);
        builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.AllowAnyOrigin()));
        builder.Services.AddSingleton<IServiceRestart, TestServiceRestart>();

        if (useMsiHardware)
        {
            builder.Services.AddMsiPlugin();
        }

        if (msi is not null)
        {
            builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, MsiJsonContext.Default));
            builder.Services.AddSingleton<ISensorPluginFactory>(new TestMsiFactory(msi));
        }

        if (useInstalledFactories || asus is not null)
        {
            builder.Services.AddSingleton<INvidiaHardwareSource, TestNvidiaHardwareSource>();
            builder.Services.AddGenericNvidiaPlugin();
        }

        if (useInstalledFactories)
        {
            builder.Services.AddWireViewPlugin();
            builder.Services.AddAsusPlugin();
            builder.Services.AddMsiPlugin();
        }

        if (registerTestPlugins)
        {
            builder.Services.AddSingleton<ISensorPluginFactory>(new TestSensorPluginFactory("healthy"));
            builder.Services.AddSingleton<ISensorPluginFactory>(new TestSensorPluginFactory("broken", TestPluginBehavior.ThrowOnPoll));
            builder.Services.AddSingleton<ISensorPluginFactory>(new TestSensorPluginFactory("slow", TestPluginBehavior.WaitForCancellation));
        }

        if (device is not null)
        {
            builder.Services.AddSingleton<WireViewStream>();
            builder.Services.AddSingleton<ISensorPluginFactory>(provider =>
                new TestWireViewFactory(device, provider.GetRequiredService<WireViewStream>()));
        }

        if (asus is not null)
        {
            builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, AsusJsonContext.Default));
            builder.Services.AddSingleton<AsusStream>();
            builder.Services.AddSingleton<ISensorPluginFactory>(provider => new TestAsusFactory(asus, provider.GetRequiredService<AsusStream>()));
        }

        var application = builder.Build();
        application.UseCors().UseWebSockets();
        application.MapMonitoringEndpoints();
        if (msi is not null || useMsiHardware || useInstalledFactories)
        {
            application.MapMsiEndpoints();
        }

        if (device is not null || useInstalledFactories)
        {
            application.MapWireViewEndpoints();
        }

        if (asus is not null || useInstalledFactories)
        {
            application.MapAsusEndpoints();
        }

        try
        {
            await application.StartAsync();

            var server = application.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>();
            var address = addresses!.Addresses.Single();

            return new MonitoringTestHost(application, address, configurationPath);
        }
        catch
        {
            await application.DisposeAsync();
            throw;
        }
    }
}
