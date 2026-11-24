using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RTXLens.Core.Plugins;
using RTXLens.Hardware.Nvidia;
using RTXLens.Hardware.Nvidia.Diagnostics;
using RTXLens.Plugins.GenericNvidia.Serialization;

namespace RTXLens.Plugins.GenericNvidia;

public static class ServiceCollectionEx
{
    public static IServiceCollection AddGenericNvidiaPlugin(this IServiceCollection services)
    {
        services.TryAddSingleton<INvidiaFallback, LibreGpuFallback>();
        services.TryAddSingleton<NativeNvidiaHardwareSource>();
        services.TryAddSingleton<INvidiaHardwareSource>(provider => provider.GetRequiredService<NativeNvidiaHardwareSource>());
        services.TryAddSingleton<NvidiaTelemetryReader>();
        services.TryAddSingleton<INvidiaDiagnosticsReader>(provider => provider.GetRequiredService<NativeNvidiaHardwareSource>());
        services.AddSingleton<ISensorPluginFactory, GenericNvidiaPluginFactory>();
        
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, GenericNvidiaJsonContext.Default));

        return services;
    }
}
