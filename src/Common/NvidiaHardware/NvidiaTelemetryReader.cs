using RTXLens.Contracts.Sensors;
using RTXLens.Hardware.Nvidia.Diagnostics;
using RTXLens.Hardware.Nvidia.Mapping;

namespace RTXLens.Hardware.Nvidia;

public sealed class NvidiaTelemetryReader(INvidiaHardwareSource source, TimeProvider clock) : IDisposable
{
    private readonly Lock _lock = new();
    private NvidiaDevice[] _devices = [];
    private DateTimeOffset _discoveredAt = DateTimeOffset.MinValue;
    private bool _disposed;
    private NvidiaSample? _latestGpu;
    private TimeSpan _gpuFreshness;

    public void ClearGpu()
    {
        lock (_lock)
        {
            _latestGpu = null;
        }
    }

    public NvidiaSample? ReadGpu(string? deviceId, TimeSpan staleAfter,
        INvidiaDiagnosticsReader? diagnostics, CancellationToken token, out SensorReading[] status)
    {
        lock (_lock)
        {
            _latestGpu = null;
            status = [];
            token.ThrowIfCancellationRequested();
            if (Select(GetDevices(), deviceId) is not { } device) return null;
            var sensors = source.Read(device);
            var sample = new NvidiaSample(device, clock.GetUtcNow(), sensors);
            var clocks = source.ReadDomainClocks(device);
            status = GenericNvidiaSnapshotMapper.Create(sample, new NvidiaSample(device, clock.GetUtcNow(), clocks));
            if (diagnostics?.Read(sample.Device.Id) is { } diagnostic)
            {
                status = [.. status, .. NvidiaDiagnosticsMapper.Create(diagnostic)];
            }
            token.ThrowIfCancellationRequested();
            _latestGpu = sample;
            _gpuFreshness = staleAfter;
            return sample;
        }
    }

    public NvidiaSample? ReadAstralPins(string deviceId, CancellationToken token, out NvidiaSample? cachedGpu)
    {
        lock (_lock)
        {
            token.ThrowIfCancellationRequested();
            cachedGpu = null;
            if (Select(GetDevices(), deviceId) is not { IsAstral: true } device) return null;
            var pins = source.ReadPins(device);
            token.ThrowIfCancellationRequested();
            var now = clock.GetUtcNow();
            if (_latestGpu is { } gpu && gpu.Device.Id == device.Id && now - gpu.ObservedAt <= _gpuFreshness)
            {
                cachedGpu = gpu;
            }
            return NvidiaSample.Create(device, now, pins);
        }
    }

    private static NvidiaDevice? Select(NvidiaDevice[] devices, string? id) => id is null
        ? devices.FirstOrDefault()
        : devices.FirstOrDefault(device => device.Id == id || device.LegacyId == id);

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
                _latestGpu = null;
                _devices = [];
                _discoveredAt = DateTimeOffset.MinValue;

                source.Reset();
                throw;
            }
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
                ClearGpu();
                break;
        }

        _devices = source.Discover();
        if (_latestGpu is { } gpu && !_devices.Contains(gpu.Device))
        {
            _latestGpu = null;
        }

        if (_devices.Length == 0)
        {
            source.Reset();
        }

        _discoveredAt = clock.GetUtcNow();
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

            _devices = [];

            source.Dispose();
        }
    }
}
