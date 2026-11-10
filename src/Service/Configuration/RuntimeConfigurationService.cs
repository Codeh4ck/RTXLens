using RTXLens.Core.Configuration;
using RTXLens.Core.Diagnostics;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Monitoring;
using RTXLens.Core.Plugins;
using RTXLens.Core.Utilities;

namespace RTXLens.Service.Configuration;

public sealed class RuntimeConfigurationService(PluginRuntimeManager runtime, RuntimeConfigurationFile file,
    IConfiguration bootstrap, ILogger<RuntimeConfigurationService> logger) : BackgroundService, IRuntimeConfiguration
{
    private readonly SemaphoreSlim _gate = new(1);
    private readonly AsyncSignal _reload = new();
    private ConfigurationSnapshot _current = new(0, new ServiceConfiguration());
    private FileSystemWatcher? _watcher;
    private string? _observedContents;
    private readonly string _epoch = Guid.NewGuid().ToString("N");

    public ConfigurationSnapshot Current => Volatile.Read(ref _current);
    public event Action? Changed;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(file.Path))!;
        Directory.CreateDirectory(directory);

        ServiceConfiguration configuration;
        var persistDefaults = false;

        if (File.Exists(file.Path))
        {
            _observedContents = await ReadContentsAsync(cancellationToken);

            var original = RuntimeJson.Deserialize(_observedContents);
            configuration = runtime.MigrateConfiguration(original);
            persistDefaults |= RuntimeJson.Serialize(original) != RuntimeJson.Serialize(configuration);

            foreach (var (id, settings) in runtime.CreateDefaults().Plugins)
            {
                persistDefaults |= configuration.Plugins.TryAdd(id, settings);
            }
        }
        else
        {
            configuration = runtime.CreateDefaults(bootstrap.GetSection("Plugins")) with
            {
                Monitoring = bootstrap.GetSection("Monitoring").Get<MonitoringOptions>() ?? new MonitoringOptions(),
                Warnings = RuntimeJson.ReadWarningOptions(bootstrap.GetSection("Warnings"))
            };

            persistDefaults = true;
        }

        var upgraded = runtime.WarningRegistry.Migrate(configuration);

        persistDefaults |= RuntimeJson.Serialize(upgraded) != RuntimeJson.Serialize(configuration);
        configuration = upgraded;

        if (configuration.Monitoring.ConnectorProfiles.Version == 0)
        {
            configuration.Monitoring.Selections.Clear();
            configuration.Monitoring.ConnectorProfiles = runtime.ConnectorProfiles.CreateDefaults();
            persistDefaults = true;
        }

        runtime.Validate(configuration);
        if (persistDefaults)
        {
            _observedContents = RuntimeJson.Serialize(configuration);
            await PersistAsync(_observedContents, cancellationToken);
        }

        await runtime.ApplyAsync(configuration, cancellationToken);

        Volatile.Write(ref _current, new ConfigurationSnapshot(1, configuration, Version: CreateVersion(1)));

        _watcher = new FileSystemWatcher(directory, Path.GetFileName(file.Path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
        };

        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Deleted += OnFileChanged;
        _watcher.Renamed += OnFileChanged;
        _watcher.Error += (_, _) => _reload.Set();

        _watcher.EnableRaisingEvents = true;

        logger.LogActivity(LogLevel.Information, "configuration.loaded", "Configuration",
            persistDefaults ? "Service configuration loaded and defaults or migrations saved." : "Service configuration loaded.");

        await base.StartAsync(cancellationToken);
    }

    public async Task<ConfigurationSnapshot> UpdateAsync(Func<ServiceConfiguration, ServiceConfiguration> update,
        CancellationToken cancellationToken, string? expectedVersion = null)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var previous = Current;
            if (expectedVersion is not null && expectedVersion != previous.Version)
            {
                throw new ConfigurationConflictException();
            }

            var candidate = runtime.WarningRegistry.Migrate(RuntimeJson.Clone(update(RuntimeJson.Clone(previous.Configuration))));
            if (candidate.Monitoring.ConnectorProfiles.Version == 0)
            {
                candidate.Monitoring.ConnectorProfiles = RuntimeJson.Clone(previous.Configuration).Monitoring.ConnectorProfiles;
            }

            runtime.Validate(candidate);
            var contents = RuntimeJson.Serialize(candidate);
            try
            {
                await PersistAsync(contents, cancellationToken);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                logger.LogActivity(LogLevel.Error, "configuration.save-failed", "Configuration",
                    "The service could not save its settings.", eventViewer: true, exception: error);
                throw;
            }

            // Once committed to disk, finish applying even if the HTTP caller disconnects.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await runtime.ApplyAsync(candidate, deadline.Token);
            }
            catch
            {
                await RestoreAsync(previous.Configuration);
                throw;
            }

            _observedContents = contents;
            Volatile.Write(ref _current, new ConfigurationSnapshot(previous.Revision + 1, candidate, Version: CreateVersion(previous.Revision + 1)));
            Changed?.Invoke();
            logger.LogActivity(LogLevel.Information, "configuration.applied", "Configuration", "Service settings applied.",
                $"Revision {Current.Revision}.");
            return Current;
        }
        catch (ArgumentException)
        {
            logger.LogActivity(LogLevel.Warning, "configuration.apply-rejected", "Configuration", "The service rejected invalid settings.");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Polling also recovers missed notifications and atomic file replacements.
                await _reload.WaitAsync(1000, stoppingToken);
                await ReloadAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var contents = await ReadContentsAsync(cancellationToken);
            if (contents == _observedContents && Current.LastError is null)
            {
                return;
            }

            var candidate = runtime.WarningRegistry.Migrate(runtime.MigrateConfiguration(RuntimeJson.Deserialize(contents)));
            if (candidate.Monitoring.ConnectorProfiles.Version == 0)
            {
                candidate.Monitoring.ConnectorProfiles = RuntimeJson.Clone(Current.Configuration).Monitoring.ConnectorProfiles;
            }

            runtime.Validate(candidate);
            var previous = Current;

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await runtime.ApplyAsync(candidate, deadline.Token);
            }
            catch
            {
                await RestoreAsync(previous.Configuration);
                throw;
            }

            _observedContents = contents;
            Volatile.Write(ref _current, new ConfigurationSnapshot(previous.Revision + 1, candidate, Version: CreateVersion(previous.Revision + 1)));
            Changed?.Invoke();
            logger.LogActivity(LogLevel.Information, "configuration.reloaded", "Configuration", "Service settings reloaded from disk.",
                $"Revision {Current.Revision}.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (Current.LastError != exception.Message)
            {
                logger.LogActivity(LogLevel.Warning, "configuration.reload-rejected", "Configuration",
                    "Settings from disk were rejected. The service retained the last valid configuration.",
                    eventViewer: true, exception: exception);
                Volatile.Write(ref _current, Current with { LastError = exception.Message });
                Changed?.Invoke();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RestoreAsync(ServiceConfiguration configuration)
    {
        try
        {
            var contents = RuntimeJson.Serialize(configuration);
            await PersistAsync(contents, CancellationToken.None);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await runtime.ApplyAsync(configuration, timeout.Token);

            _observedContents = contents;
            logger.LogActivity(LogLevel.Warning, "configuration.rolled-back", "Configuration",
                "Applying new settings failed. The previous settings were restored.", eventViewer: true);
        }
        catch (Exception error)
        {
            logger.LogActivity(LogLevel.Error, "configuration.rollback-failed", "Configuration",
                "The previous settings could not be restored.", eventViewer: true, exception: error);
            throw;
        }
    }

    private async Task PersistAsync(string contents, CancellationToken cancellationToken)
    {
        var temporaryPath = file.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, contents, cancellationToken);
            File.Move(temporaryPath, file.Path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private async Task<string> ReadContentsAsync(CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);

        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private void OnFileChanged(object sender, FileSystemEventArgs args) => _reload.Set();
    private string CreateVersion(long revision) => $"{_epoch}:{revision}";

    public override void Dispose()
    {
        _watcher?.Dispose();
        base.Dispose();
        _gate.Dispose();
    }
}
