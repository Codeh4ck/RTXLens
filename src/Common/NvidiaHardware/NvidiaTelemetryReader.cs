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
    private SensorReading[] _lastGpuReadings = [];

    private readonly Selection _gpuSelection = new();
    private bool _awaitingPinSample;
    private readonly Selection _pinSelection = new();
    private readonly Dictionary<string, DateTimeOffset> _retryAfter = [];

    private sealed class Selection
    {
        private string? _requested;
        private string? _pciId;

        public void Forget() => _pciId = null;

        public NvidiaDevice? Resolve(IEnumerable<NvidiaDevice> devices, string? requested)
        {
            if (_requested != requested)
            {
                _requested = requested;
                _pciId = null;
            }

            var device = _pciId is null
                ? devices.FirstOrDefault(item => requested is null || item.Id == requested || item.LegacyId == requested)
                : devices.FirstOrDefault(item => item.Id == _pciId);

            _pciId = device?.Id ?? _pciId;
            return device;
        }
    }

    public void ClearGpu(bool forgetSelection = false)
    {
        lock (_lock)
        {
            _latestGpu = null;
            _lastGpuReadings = [];

            if (forgetSelection)
            {
                _gpuSelection.Forget();
            }
        }
    }

    public void ClearPinSelection()
    {
        lock (_lock)
        {
            _pinSelection.Forget();
            _awaitingPinSample = false;
        }
    }

    public NvidiaSample? ReadGpu(string? deviceId, TimeSpan staleAfter, INvidiaDiagnosticsReader? diagnostics, CancellationToken token, out SensorReading[] status, NvidiaPollTimings? timings = null)
    {
        lock (_lock)
        {
            _latestGpu = null;
            status = [];

            token.ThrowIfCancellationRequested();

            if (_gpuSelection.Resolve(GetDevices(), deviceId) is not { } device || IsWaitingForDriver(device.Id))
            {
                return null;
            }

            try
            {
                NvidiaSensor[] sensors;
                using (timings?.Measure("GPU readings"))
                {
                    sensors = source.Read(device);
                }

                var sample = new NvidiaSample(device, clock.GetUtcNow(), sensors);
                NvidiaSensor[] clocks;
                using (timings?.Measure("internal clocks"))
                {
                    clocks = source.ReadDomainClocks(device);
                }

                status = GenericNvidiaSnapshotMapper.Create(sample, new NvidiaSample(device, clock.GetUtcNow(), clocks));

                if (diagnostics is not null)
                {
                    Plugins.GenericNvidia.Contracts.Devices.NvidiaDiagnostics? diagnostic;
                    using (timings?.Measure("NVML diagnostics"))
                    {
                        diagnostic = diagnostics.Read(device.Id);
                    }

                    if (diagnostic is not null)
                    {
                        status = [.. status, .. NvidiaDiagnosticsMapper.Create(diagnostic)];
                    }
                }

                token.ThrowIfCancellationRequested();

                if (status.Any(reading => reading.Quality == Contracts.Enums.ReadingQuality.Valid))
                {
                    if (_lastGpuReadings.Length > 0 && _lastGpuReadings[0].Identity.DeviceId == device.Id)
                    {
                        var current = status.Select(reading => reading.Identity).ToHashSet();

                        status = [.. status, .. _lastGpuReadings.Where(reading => !current.Contains(reading.Identity))
                            .Select(reading => reading with { Quality = Contracts.Enums.ReadingQuality.Unavailable, Value = null })];
                    }
                    _lastGpuReadings = status;
                }
                else
                {
                    _lastGpuReadings = [];
                }

                _latestGpu = sample;
                _gpuFreshness = staleAfter;

                return sample;
            }
            catch (NvidiaSessionLostException)
            {
                InvalidateSession(device.Id);
                status = [];
                return null;
            }
        }
    }

    public NvidiaSample? ReadAstralPins(string? deviceId, CancellationToken token, out NvidiaSample? cachedGpu, NvidiaPollTimings? timings = null)
    {
        lock (_lock)
        {
            token.ThrowIfCancellationRequested();

            cachedGpu = null;
            if (_pinSelection.Resolve(GetDevices().Where(item => item.IsAstral), deviceId) is not { } device || IsWaitingForDriver(device.Id))
            {
                _awaitingPinSample = true;
                return null;
            }

            NvidiaSensor[] pins;
            try
            {
                using (timings?.Measure("Astral pin reads"))
                {
                    pins = source.ReadPins(device);
                }
            }
            catch (NvidiaSessionLostException)
            {
                InvalidateSession(device.Id);
                _awaitingPinSample = true;
                return null;
            }

            token.ThrowIfCancellationRequested();

            if (_awaitingPinSample && pins.Length == 0)
            {
                return null;
            }

            _awaitingPinSample = false;

            var now = clock.GetUtcNow();
            if (_latestGpu is { } gpu && gpu.Device.Id == device.Id && now - gpu.ObservedAt <= _gpuFreshness)
            {
                cachedGpu = gpu;
            }

            return NvidiaSample.Create(device, now, pins);
        }
    }

    private bool IsWaitingForDriver(string deviceId)
    {
        if (!_retryAfter.TryGetValue(deviceId, out var retry))
        {
            return false;
        }

        if (clock.GetUtcNow() < retry)
        {
            return true;
        }

        _retryAfter.Remove(deviceId);
        return false;
    }

    private void InvalidateSession(string? deviceId)
    {
        _latestGpu = null;
        _lastGpuReadings = [];
        _devices = [];

        if (deviceId is not null)
        {
            _retryAfter[deviceId] = clock.GetUtcNow() + TimeSpan.FromSeconds(5);
        }

        _discoveredAt = deviceId is null ? clock.GetUtcNow() : DateTimeOffset.MinValue;
        source.Reset();
    }

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
            catch (NvidiaSessionLostException)
            {
                InvalidateSession(null);
                return [];
            }
            catch
            {
                _latestGpu = null;
                _lastGpuReadings = [];
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
            _lastGpuReadings = [];
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
