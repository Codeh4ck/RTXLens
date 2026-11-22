using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RTXLens.Contracts.Discovery;
using RTXLens.Contracts.Configuration;
using RTXLens.Warnings;

namespace RTXLens.Core.Plugins;

public sealed class PluginRuntimeManager : IAsyncDisposable
{
    private readonly Dictionary<string, PluginWorker> _workers;
    private readonly ISensorPluginFactory[] _factories;

    private readonly Lock _shutdownLock = new();
    private Task? _shutdown;
    public IReadOnlyList<ISensorPluginFactory> Factories => _factories;
    public ConnectorProfileResolver ConnectorProfiles { get; }
    public WarningRegistry WarningRegistry { get; }
    public PluginDefinition[] Definitions => [.. field];

    public PluginRuntimeManager(IEnumerable<ISensorPluginFactory> factories, SensorStateStore store, TimeProvider clock, ILogger<PluginRuntimeManager> logger)
    {
        var installed = factories.ToArray();

        _factories = installed;
        ConnectorProfiles = new ConnectorProfileResolver(installed.OfType<IConnectorSourceProvider>());

        if (installed.Any(factory => factory.WarningSource is { } source && source.PluginId != factory.Id))
        {
            throw new ArgumentException("A warning source must belong to its plugin factory.");
        }

        WarningRegistry = new WarningRegistry(installed.Select(factory => factory.WarningSource).OfType<IWarningSource>());
        _workers = installed.ToDictionary(factory => factory.Id, factory => new PluginWorker(factory, store, clock, logger), StringComparer.Ordinal);

        Definitions =
        [
            .. installed.Select(factory => new PluginDefinition(factory.Id, factory.Name, factory.Capabilities)
            {
                Hardware = factory.Hardware,
                ConnectorMetrics = factory is IConnectorSourceProvider provider
                    ? [.. Enum.GetValues<Contracts.Enums.SensorMetric>().Where(metric => provider.Priority(metric) is not null)] : [],
                Warnings = [.. WarningRegistry.Definitions.Where(definition => definition.PluginId == factory.Id)]
            })
        ];
    }

    public ServiceConfiguration CreateDefaults(IConfiguration? bootstrap = null)
    {
        if (bootstrap is not null && bootstrap.GetChildren().Any(section => !_workers.ContainsKey(section.Key)))
        {
            throw new ArgumentException("Bootstrap configuration contains an unknown plugin ID.");
        }

        return new ServiceConfiguration
        {
            Plugins = _workers.ToDictionary(pair => pair.Key, pair => pair.Value.CreateDefaultSettings(bootstrap?.GetSection(pair.Key)), StringComparer.Ordinal)
        };
    }

    public ServiceConfiguration MigrateConfiguration(ServiceConfiguration configuration) =>
        _factories.Aggregate(configuration, (current, factory) => factory.MigrateConfiguration(current));

    public void Validate(ServiceConfiguration configuration)
    {
        if (!configuration.Warnings.IsValid() || !configuration.Monitoring.IsValid() || configuration.Widget?.IsValid() != true)
        {
            throw new ArgumentException("Invalid service configuration.");
        }

        if (configuration.Plugins.Count != _workers.Count || configuration.Plugins.Keys.Any(id => !_workers.ContainsKey(id)))
        {
            throw new ArgumentException("Configuration must contain exactly the installed plugin IDs.");
        }

        if (configuration.Plugins.Values.Any(settings => !settings.IsValid()))
        {
            throw new ArgumentException("Invalid plugin polling or freshness interval.");
        }

        foreach (var (id, settings) in configuration.Plugins)
        {
            _workers[id].ValidateSettings(settings);
        }
    }

    public void Start(ServiceConfiguration configuration)
    {
        foreach (var (id, settings) in configuration.Plugins)
        {
            _workers[id].Start(settings);
        }
    }

    public async Task ApplyAsync(ServiceConfiguration configuration, CancellationToken cancellationToken)
    {
        Validate(configuration);
        foreach (var (id, settings) in configuration.Plugins)
        {
            await _workers[id].ApplyAsync(settings, cancellationToken);
        }
    }

    public Task<T> ExecuteAsync<T>(string pluginId, Func<ISensorPlugin, T> action, CancellationToken cancellationToken) =>
        GetWorker(pluginId).ExecuteAsync(action, cancellationToken);

    public Task RestartAsync(string pluginId, CancellationToken cancellationToken) => GetWorker(pluginId).RestartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_shutdownLock)
        {
            _shutdown ??= Task.WhenAll(_workers.Values.Select(worker => worker.StopAsync(CancellationToken.None)));
            return _shutdown.WaitAsync(cancellationToken);
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync(CancellationToken.None));

    private PluginWorker GetWorker(string pluginId) => !_workers.TryGetValue(pluginId, out var worker)
        ? throw new KeyNotFoundException($"Plugin '{pluginId}' is not installed.")
        : worker;
}