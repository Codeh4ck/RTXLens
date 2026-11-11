using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RTXLens.Core.Plugins;
using RTXLens.Hardware.Nvidia;
using RTXLens.Plugins.GenericNvidia.Diagnostics;
using RTXLens.Plugins.GenericNvidia.Serialization;

namespace RTXLens.Plugins.GenericNvidia;

public static class ServiceCollectionEx
{
    public static IServiceCollection AddGenericNvidiaPlugin(this IServiceCollection services)
    {
        services.TryAddSingleton<INvidiaHardwareSource, LibreNvidiaHardwareSource>();
        services.TryAddSingleton<NvidiaTelemetryReader>();
        services.TryAddSingleton<INvidiaDiagnosticsReader, NvidiaDiagnosticsReader>();
        services.AddSingleton<ISensorPluginFactory, GenericNvidiaPluginFactory>();
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, GenericNvidiaJsonContext.Default));
        return services;
    }
}
