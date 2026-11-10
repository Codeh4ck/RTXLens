using RTXLens.Core.Configuration;
using RTXLens.Core;
using RTXLens.Core.Plugins;
using RTXLens.Core.Streaming;
using RTXLens.Service.Configuration;
using RTXLens.Service.Diagnostics;
using RTXLens.Service.Services;
using RTXLens.Service.Utilities;

namespace RTXLens.Service;

public static class MonitoringServices
{
    public static IServiceCollection AddMonitoringCore(this IServiceCollection services, IConfiguration configuration, bool debugInstance = false)
    {
        var defaultDirectory = debugInstance
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RTXLens.Debug")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RTXLens");

        var configurationPath = Path.GetFullPath(configuration["RuntimeConfiguration:FilePath"] ?? Path.Combine(defaultDirectory, "appsettings.json"));

        services.AddSingleton(new RuntimeConfigurationFile(configurationPath));
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new MonitoringServiceLifetime(TimeProvider.System));
        services.AddSingleton(_ => new SensorStateStore([]));
        services.AddSingleton<SensorStreamHub>();
        services.AddSingleton<PluginRuntimeManager>();
        services.AddHostedService(provider => provider.GetRequiredService<PluginRuntimeManager>());
        services.AddSingleton<ActivityLoggerProvider>();
        services.AddSingleton<ILoggerProvider>(provider => provider.GetRequiredService<ActivityLoggerProvider>());
        services.AddLogging(logging => logging.AddFilter<ActivityLoggerProvider>(null, LogLevel.Information));
        services.AddSingleton<ServiceLogJournal>();
        services.AddSingleton<RuntimeConfigurationService>();
        services.AddSingleton<IRuntimeConfiguration>(provider => provider.GetRequiredService<RuntimeConfigurationService>());
        services.AddHostedService<ServiceLogJournal>(provider => provider.GetRequiredService<ServiceLogJournal>());
        services.AddHostedService(provider => provider.GetRequiredService<RuntimeConfigurationService>());
        services.AddSingleton<ActiveSettingsProfileState>();

        var debugProfiles = debugInstance ? Path.Combine(Path.GetDirectoryName(configurationPath)!, "profiles") : null;

        services.AddSingleton(provider => new SettingsProfileStore(configuration, provider.GetRequiredService<ILogger<SettingsProfileStore>>(), debugProfiles));
        services.AddSingleton<SettingsProfileResolver>();
        services.AddSingleton<DeviceWarningProfileStore>();
        services.AddSingleton<IWarningShutdown, WindowsWarningShutdown>();
        services.AddSingleton<WarningShutdownCoordinator>();
        services.AddSingleton<SafeguardJournalService>();

        if (!debugInstance)
        {
            // Only the installed service should consume the machine-wide Afterburner job queue.
            services.AddHostedService(provider => provider.GetRequiredService<SafeguardJournalService>());
        }

        services.AddSingleton<MonitoringActivity>();
        services.AddHostedService<SnapshotBroadcastService>();
        services.AddHostedService<HardwareDiscoveryService>();
        services.AddSingleton<IServiceRestart, ServiceRestart>();

        return services;
    }
}
