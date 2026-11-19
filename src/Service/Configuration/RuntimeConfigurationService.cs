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
            await CommitAsync(candidate, null, cancellationToken);
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
        var previous = Current;
        string? contents = null;
        try
        {
            contents = await ReadContentsAsync(cancellationToken);
            if (contents == _observedContents)
            {
                return;
            }
            _observedContents = contents;
            var candidate = runtime.WarningRegistry.Migrate(runtime.MigrateConfiguration(RuntimeJson.Deserialize(contents)));
            await CommitAsync(candidate, contents, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (contents is null)
            {
                // The file may reappear with the same contents after a transient read failure.
                _observedContents = null;
            }
            if (ReferenceEquals(Current, previous) && Current.LastError != exception.Message)
            {
                logger.LogActivity(LogLevel.Warning, "configuration.reload-rejected", "Configuration",
                    "Settings from disk were rejected. The service retained the last valid configuration.",
                    eventViewer: true, exception: exception);
                Publish(Current with { LastError = exception.Message });
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CommitAsync(ServiceConfiguration candidate, string? fileContents, CancellationToken cancellationToken)
    {
        var previous = Current;
        if (candidate.Monitoring.ConnectorProfiles.Version == 0)
        {
            candidate.Monitoring.ConnectorProfiles = RuntimeJson.Clone(previous.Configuration).Monitoring.ConnectorProfiles;
        }

        runtime.Validate(candidate);
        var fromDisk = fileContents is not null;
        var contents = fileContents ?? RuntimeJson.Serialize(candidate);
        if (!fromDisk)
        {
            await SaveUpdateAsync(contents, cancellationToken);
        }

        // After persisting an API update, finish applying even if the caller disconnects.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(fromDisk ? cancellationToken : CancellationToken.None);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var revision = previous.Revision + 1;
        try
        {
            await runtime.ApplyAsync(candidate, deadline.Token);
        }
        catch (Exception applyError)
        {
            _observedContents = contents;
            var failure = await RecoverAsync(applyError, revision, async () =>
            {
                var restored = RuntimeJson.Serialize(previous.Configuration);
                await PersistAsync(restored, CancellationToken.None);
                _observedContents = restored;
            }, async () =>
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await runtime.ApplyAsync(previous.Configuration, timeout.Token);
            });
            var detail = failure?.Message ?? $"Applying revision {revision} failed: {applyError.Message}. Disk and runtime were restored to revision {previous.Revision}.";
            logger.LogActivity(failure is null ? LogLevel.Warning : LogLevel.Error,
                failure is null ? "configuration.rolled-back" : "configuration.rollback-failed", "Configuration",
                detail, eventViewer: true, exception: (Exception?)failure ?? applyError);
            Publish(previous with { LastError = detail });
            if (failure is not null)
            {
                throw failure;
            }
            throw;
        }

        _observedContents = contents;
        Publish(new ConfigurationSnapshot(revision, candidate, Version: CreateVersion(revision)));
        logger.LogActivity(LogLevel.Information, fromDisk ? "configuration.reloaded" : "configuration.applied",
            "Configuration", fromDisk ? "Service settings reloaded from disk." : "Service settings applied.", $"Revision {revision}.");
    }

    internal static async Task<AggregateException?> RecoverAsync(Exception applyError, long revision,
        Func<Task> restoreFile, Func<Task> restoreRuntime)
    {
        List<Exception> errors = [applyError];
        async Task<string> Restore(string target, Func<Task> operation)
        {
            try
            {
                await operation();
                return $"{target} restored";
            }
            catch (Exception error)
            {
                errors.Add(error);
                return $"{target} restore failed: {error.Message}";
            }
        }

        var disk = await Restore("Disk", restoreFile);
        var hardware = await Restore("Runtime", restoreRuntime);
        return errors.Count == 1 ? null : new AggregateException(
            $"Applying revision {revision} failed: {applyError.Message}. {disk}. {hardware}.", errors);
    }

    private void Publish(ConfigurationSnapshot snapshot)
    {
        Volatile.Write(ref _current, snapshot);
        if (Changed is not { } handlers)
        {
            return;
        }
        foreach (Action notify in handlers.GetInvocationList())
        {
            try
            {
                notify();
            }
            catch (Exception error)
            {
                logger.LogActivity(LogLevel.Error, "configuration.notification-failed", "Configuration",
                    $"A listener failed after revision {snapshot.Revision} was published.", exception: error);
            }
        }
    }

    private async Task SaveUpdateAsync(string contents, CancellationToken cancellationToken)
    {
        try
        {
            await PersistAsync(contents, cancellationToken);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            var detail = $"Could not save revision {Current.Revision + 1}: {error.Message}";
            logger.LogActivity(LogLevel.Error, "configuration.save-failed", "Configuration",
                "The service could not save its settings.", detail, eventViewer: true, exception: error);
            Publish(Current with { LastError = detail });
            throw;
        }
    }

    private async Task PersistAsync(string contents, CancellationToken cancellationToken)
    {
        var temporaryPath = file.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, contents, cancellationToken);
            await ReplaceFileAsync(temporaryPath, cancellationToken);
        }
        catch
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(cleanup, "Could not remove a temporary configuration file.");
            }
            throw;
        }
    }

    private async Task ReplaceFileAsync(string temporaryPath, CancellationToken cancellationToken)
    {
        // A Windows reader can briefly block renaming the destination. Retry only the atomic move.
        for (var delayMs = 20; ; delayMs *= 2)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporaryPath, file.Path, overwrite: true);
                return;
            }
            catch (Exception error) when (OperatingSystem.IsWindows() && delayMs <= 160 &&
                (error is UnauthorizedAccessException || error is IOException && (error.HResult & 0xffff) is 32 or 33))
            {
                await Task.Delay(delayMs, cancellationToken);
            }
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
