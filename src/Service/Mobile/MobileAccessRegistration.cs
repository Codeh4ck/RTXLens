using System.Net;
using System.Runtime.Versioning;
using RTXLens.Core.Streaming;
using RTXLens.Contracts.Mobile;
using RTXLens.Service.Configuration;
using RTXLens.Service.Services;

namespace RTXLens.Service.Mobile;

[SupportedOSPlatform("windows")]
internal static class MobileAccessRegistration
{
    public static void AddMobileAccess(this IServiceCollection services, bool debugInstance)
    {
        services.AddSingleton<IMobileAccessStore>(new MobileAccessStore(debugInstance));
        services.AddSingleton<IMobileNetworkAccess, MobileNetworkAccess>();

        services.AddSingleton(provider => new MobilePairing(provider.GetRequiredService<IMobileAccessStore>(),
            provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<MobilePairing>>()));

        services.AddSingleton(_ => NoIpUpdater.CreateClient());

        services.AddSingleton(provider => new NoIpUpdater(provider.GetRequiredService<HttpClient>(),
            provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<NoIpUpdater>>()));

        services.AddSingleton(provider => new MobileWarningHistory(
            Path.Combine(Path.GetDirectoryName(provider.GetRequiredService<RuntimeConfigurationFile>().Path)!, "mobile-warning-history"),
            provider.GetRequiredService<ILogger<MobileWarningHistory>>()));

        services.AddSingleton(provider => new MobileHistoryEndpoints(provider.GetRequiredService<MobileWarningHistory>(),
            provider.GetRequiredService<SafeguardJournalService>(), provider.GetRequiredService<TimeProvider>(),
            () => provider.GetRequiredService<MobilePairing>().Data.Settings.WarningHistoryDays));

        services.AddHttpClient<ExpoMobilePushProvider>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.MaxResponseContentBufferSize = 65536;
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

        services.AddSingleton<MobilePushDelivery>();
        services.AddHostedService(provider => provider.GetRequiredService<MobilePushDelivery>());

        services.AddSingleton(provider => new MobileAccessService(
            provider.GetRequiredService<MobilePairing>(), provider.GetRequiredService<SensorStreamHub>(),
            provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<NoIpUpdater>(),
            provider.GetRequiredService<IMobileNetworkAccess>(), provider.GetRequiredService<ILogger<MobileAccessService>>(),
            IPAddress.Any, debugInstance, provider.GetRequiredService<MobileWarningHistory>(),
            provider.GetRequiredService<MobileHistoryEndpoints>(), provider.GetRequiredService<MobilePushDelivery>()));

        services.AddHostedService(provider => provider.GetRequiredService<MobileAccessService>());
        services.AddHostedService<MobileNoIpService>();

        var pipeName = MobileAccessProtocol.GetPipeName(debugInstance);
        services.AddHostedService(provider => new MobileAccessAdminPipe(
            pipeName, provider.GetRequiredService<MobileAccessService>(), provider.GetRequiredService<MobilePairing>(),
            provider.GetRequiredService<ILogger<MobileAccessAdminPipe>>()));
    }

    private sealed class MobileNoIpService(MobilePairing pairing, NoIpUpdater updater) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));

            do
            {
                var settings = pairing.Data.Settings;
                await updater.CheckAsync(settings.Enabled ? settings.NoIp : null, stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
    }
}
