namespace RTXLens.Hardware.Nvidia;

public sealed class NvidiaTelemetryReader(INvidiaHardwareSource source, TimeProvider clock) : IDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, NvidiaSample> _samples = new(StringComparer.Ordinal);
    private NvidiaDevice[] _devices = [];
    private DateTimeOffset _discoveredAt = DateTimeOffset.MinValue;
    private bool _disposed;

    public NvidiaDevice[] GetDevices(bool refresh = false)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                Discover(refresh);
                return [.. _devices];
            }
            catch
            {
                _samples.Clear();
                _devices = [];
                _discoveredAt = DateTimeOffset.MinValue;
                source.Reset();
                throw;
            }
        }
    }

    public NvidiaSample? Read(string? deviceId, int pollIntervalMs, CancellationToken token)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();

            try
            {
                Discover(false);

                var device = deviceId is null ? _devices.FirstOrDefault() : _devices.FirstOrDefault(item => item.Id == deviceId || item.LegacyId == deviceId);

                if (device is null)
                {
                    return null;
                }

                var now = clock.GetUtcNow();
                if (_samples.TryGetValue(device.Id, out var sample) && now - sample.ObservedAt < TimeSpan.FromMilliseconds(pollIntervalMs))
                {
                    return sample;
                }

                var sensors = source.Read(device);
                token.ThrowIfCancellationRequested();

                sample = new NvidiaSample(device, clock.GetUtcNow(), sensors);
                _samples[device.Id] = sample;

                return sample;
            }
            catch
            {
                _samples.Clear();

                _devices = [];
                _discoveredAt = DateTimeOffset.MinValue;

                source.Reset();
                throw;
            }
        }
    }

    public NvidiaSample ReadDomainClocks(NvidiaDevice device)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var sensors = source.ReadDomainClocks(device);
            return new NvidiaSample(device, clock.GetUtcNow(), sensors);
        }
    }

    private void Discover(bool refresh)
    {
        switch (refresh)
        {
            case false when clock.GetUtcNow() - _discoveredAt < TimeSpan.FromSeconds(5):
                return;
            case true:
                source.Reset();
                _samples.Clear();
                break;
        }

        _devices = source.Discover();

        if (_devices.Length == 0)
        {
            source.Reset();
        }

        _discoveredAt = clock.GetUtcNow();

        foreach (var id in _samples.Keys.Where(id => _devices.All(device => device.Id != id)).ToArray())
        {
            _samples.Remove(id);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _samples.Clear();
            _devices = [];

            source.Dispose();
        }
    }
}
