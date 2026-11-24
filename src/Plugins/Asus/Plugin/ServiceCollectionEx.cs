using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RTXLens.Hardware.Nvidia;
using RTXLens.Core.Plugins;
using RTXLens.Plugins.Asus.Factories;
using RTXLens.Plugins.Asus.Serialization;
using RTXLens.Plugins.Asus.Streaming;

namespace RTXLens.Plugins.Asus;

public static class ServiceCollectionEx
{
    public static IServiceCollection AddAsusPlugin(this IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, AsusJsonContext.Default));
        services.TryAddSingleton<NativeNvidiaHardwareSource>();
        services.TryAddSingleton<INvidiaHardwareSource>(provider => provider.GetRequiredService<NativeNvidiaHardwareSource>());
        services.TryAddSingleton<NvidiaTelemetryReader>();
        services.AddSingleton<AsusStream>();
        services.AddSingleton<ISensorPluginFactory, AsusPluginFactory>();
        return services;
    }
}
