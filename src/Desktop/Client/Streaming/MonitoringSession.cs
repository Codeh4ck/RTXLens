using RTXLens.Client.Http;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Discovery;
using RTXLens.Contracts.Diagnostics;
using RTXLens.Contracts.Sensors;

namespace RTXLens.Client.Streaming;

public sealed class MonitoringSession(IMonitoringApi api)
{
    private readonly Lock _connectionLock = new();
    private CancellationTokenSource? _connection;
    private SensorSnapshot? _latest;

    public SensorSnapshot? Latest => Volatile.Read(ref _latest);
    public event Action<ConnectionStatus>? StatusChanged;
    public event Action<PluginCatalog>? CatalogReceived;
    public event Action<ConfigurationSnapshot>? ConfigurationReceived;
    public event Action<SensorSnapshot>? SnapshotReceived;

    public void Reconnect()
    {
        lock (_connectionLock)
        {
            _connection?.Cancel();
        }
    }

    public async Task RunAsync(CancellationToken stop)
    {
        var delay = 500;
        while (!stop.IsCancellationRequested)
        {
            var connected = false;
            var reconnectCause = "stream-ended";
            var disconnectMessage = "Service disconnected; reconnecting…";
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(stop);
            lock (_connectionLock)
            {
                _connection = connection;
            }

            try
            {
                StatusChanged?.Invoke(new ConnectionStatus(false, "Connecting to monitoring service…"));
                var catalogTask = api.GetPluginsAsync(connection.Token);
                var configurationTask = api.GetConfigurationAsync(connection.Token);
                await Task.WhenAll(catalogTask, configurationTask);
                var configuration = await configurationTask;
                var catalog = await catalogTask;
                if (catalog.SchemaVersion != 1)
                {
                    throw new InvalidDataException("Unsupported plugin catalog schema.");
                }

                CatalogReceived?.Invoke(catalog);
                ConfigurationReceived?.Invoke(configuration);
                await foreach (var frame in api.ReadSensorsAsync(connection.Token))
                {
                    if (frame.ConfigurationRevision != configuration.Revision)
                    {
                        configuration = await api.GetConfigurationAsync(connection.Token);
                        ConfigurationReceived?.Invoke(configuration);
                        CatalogReceived?.Invoke(await api.GetPluginsAsync(connection.Token));
                    }

                    Volatile.Write(ref _latest, frame);
                    SnapshotReceived?.Invoke(frame);
                    if (!connected)
                    {
                        connected = true;
                        StatusChanged?.Invoke(new ConnectionStatus(true, "Service connected"));
                    }
                    delay = 500;
                }
            }
            catch (OperationCanceledException) when (connection.IsCancellationRequested)
            {
                reconnectCause = "requested";
            }
            catch (InvalidDataException error)
            {
                reconnectCause = "incompatible-data";
                disconnectMessage = $"Incompatible service data. {error.Message} Retrying…";
            }
            catch (Exception exception) when (exception is HttpRequestException or MonitoringApiException or IOException
                or System.Net.WebSockets.WebSocketException or System.Text.Json.JsonException or OperationCanceledException)
            {
                reconnectCause = exception.GetType().Name;
                disconnectMessage = exception.Message;
            }
            finally
            {
                lock (_connectionLock)
                {
                    _connection = null;
                }

                Volatile.Write(ref _latest, null);
                StatusChanged?.Invoke(new ConnectionStatus(false, disconnectMessage));
            }

            if (stop.IsCancellationRequested)
            {
                break;
            }

            TelemetryMetrics.Reconnects.Add(1, new KeyValuePair<string, object?>("cause", reconnectCause));
            try
            {
                await Task.Delay(delay + Random.Shared.Next(0, 200), stop);
                delay = Math.Min(delay * 2, 15000);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
