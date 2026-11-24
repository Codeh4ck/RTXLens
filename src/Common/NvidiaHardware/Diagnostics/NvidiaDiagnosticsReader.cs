using RTXLens.Hardware.Nvidia;
using RTXLens.Plugins.GenericNvidia.Contracts.Devices;

namespace RTXLens.Hardware.Nvidia.Diagnostics;

public sealed class NvidiaDiagnosticsReader(TimeProvider clock) : INvidiaDiagnosticsReader, IDisposable
{
    private const int PStateCount = 16;
    private const int UnknownPState = 32;
    private const uint PowerCapCounter = 74;
    private const uint SyncBoostCounter = 76;
    private const uint ReplayRolloverCounter = 95;
    private const uint SoftwareThermalCounter = 269;
    private const uint HardwareThermalCounter = 270;
    private const uint HardwarePowerBrakeCounter = 271;

    private readonly Lock _gate = new();
    private NvmlApi? _api;
    private long? _lastInitializationAttempt;
    private bool _disposed;
    private ClockCapabilities? _clockCapabilities;

    public NvidiaDiagnostics? Read(string deviceId)
    {
        if (!NvidiaPciIdentity.TryParse(deviceId, out var identity))
        {
            return null;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (Open() is not { } api || FindDevice(api, identity) is not { } handle)
            {
                return null;
            }

            return ReadDiagnostics(api, handle, deviceId);
        }
    }

    private NvmlApi? Open()
    {
        if (_api is null && (_lastInitializationAttempt is not { } lastAttempt || clock.GetElapsedTime(lastAttempt) >= TimeSpan.FromSeconds(5)))
        {
            _lastInitializationAttempt = clock.GetTimestamp();
            _api = NvmlApi.TryOpen();
        }

        return _api;
    }

    internal NvidiaSensor[] ReadBoard(string deviceId)
    {
        if (!NvidiaPciIdentity.TryParse(deviceId, out var identity))
        {
            return [];
        }

        lock (_gate)
        {
            if (Open() is not { } api || FindDevice(api, identity) is not { } handle)
            {
                return [];
            }

            var sensors = new List<NvidiaSensor>();
            var power = api.Get<NvmlApi.GetUInt>("nvmlDeviceGetPowerUsage");
            if (power is not null && power(handle, out var milliwatts) == 0)
            {
                sensors.Add(new("GPU Package", SensorType.Power, 0, milliwatts / 1000f));
            }

            var utilization = api.Get<NvmlApi.GetUtilization>("nvmlDeviceGetUtilizationRates");
            if (utilization is not null && utilization(handle, out var rates) == 0 && rates.Gpu <= 100)
            {
                sensors.Add(new("GPU Core", SensorType.Load, 0, rates.Gpu));
            }

            var memory = api.Get<NvmlApi.GetMemory>("nvmlDeviceGetMemoryInfo");
            if (memory is not null && memory(handle, out var vram) == 0 && vram.Total > 0)
            {
                const float mib = 1024 * 1024;

                sensors.Add(new("GPU Memory Total", SensorType.SmallData, 0, vram.Total / mib));
                sensors.Add(new("GPU Memory Used", SensorType.SmallData, 0, vram.Used / mib));
                sensors.Add(new("GPU Memory Free", SensorType.SmallData, 0, vram.Free / mib));
                sensors.Add(new("GPU Memory", SensorType.Load, 0, 100f * vram.Used / vram.Total));
            }

            return [.. sensors];
        }
    }

    internal void Reset()
    {
        lock (_gate)
        {
            _api?.Dispose();

            _api = null;
            _lastInitializationAttempt = null;
            _clockCapabilities = null;
        }
    }

    private NvidiaDiagnostics ReadDiagnostics(NvmlApi api, nint handle, string deviceId)
    {
        var activeReasons = api.Get<NvmlApi.GetULong>("nvmlDeviceGetCurrentClocksEventReasons") ?? api.Get<NvmlApi.GetULong>("nvmlDeviceGetCurrentClocksThrottleReasons");
        var state = Query("nvmlDeviceGetPerformanceState");
        var currentState = state < PStateCount ? (int?)state : null;
        var clockCapabilities = ReadClockCapabilities(api, handle, deviceId);

        NvmlApi.FieldValue[] fields =
        [
            new() { Id = PowerCapCounter },
            new() { Id = SoftwareThermalCounter },
            new() { Id = HardwareThermalCounter },
            new() { Id = HardwarePowerBrakeCounter },
            new() { Id = SyncBoostCounter },
            new() { Id = ReplayRolloverCounter }
        ];

        var getFields = api.Get<NvmlApi.GetFields>("nvmlDeviceGetFieldValues");
        var haveFields = getFields is not null && getFields(handle, fields.Length, fields) == 0;

        var limits = new NvidiaClockLimitDurations(Seconds(0), Seconds(1), Seconds(2), Seconds(3), Seconds(4));
        var rollover = haveFields ? fields[5].UnsignedValue : null;
        var pcie = new NvidiaPcieDiagnostics(
            Query("nvmlDeviceGetCurrPcieLinkGeneration"),
            Query("nvmlDeviceGetMaxPcieLinkGeneration"),
            Query("nvmlDeviceGetGpuMaxPcieLinkGeneration"),
            Query("nvmlDeviceGetCurrPcieLinkWidth"),
            Query("nvmlDeviceGetMaxPcieLinkWidth"),
            Query("nvmlDeviceGetPcieSpeed"),
            Query("nvmlDeviceGetPcieLinkMaxSpeed") switch
            {
                // Recognize the NVML link-speed constants and rates already expressed in Mbps.
                1 => 2500, 2 => 5000, 3 => 8000, 4 => 16000, 5 => 32000, 6 => 64000,
                >= 2500 and var rate => rate,
                _ => null
            },
            Throughput(0),
            Throughput(1),
            Query("nvmlDeviceGetPcieReplayCounter"),
            rollover <= uint.MaxValue ? (uint?)rollover : null);

        var getRange = api.Get<NvmlApi.GetPowerRange>("nvmlDeviceGetPowerManagementLimitConstraints");
        uint minimum = 0;
        uint maximum = 0;
        var haveRange = getRange is not null && getRange(handle, out minimum, out maximum) == 0;

        var powerLimits = new NvidiaPowerLimits(Query("nvmlDeviceGetPowerManagementDefaultLimit") / 1000d,
            Query("nvmlDeviceGetEnforcedPowerLimit") / 1000d, haveRange ? minimum / 1000d : null,
            haveRange ? maximum / 1000d : null);

        return new NvidiaDiagnostics(deviceId, clock.GetUtcNow(), Reasons(activeReasons), clockCapabilities.SupportedReasons, limits,
            currentState, clockCapabilities.SupportedStates, pcie, powerLimits);

        uint? Query(string symbol) => api.Get<NvmlApi.GetUInt>(symbol) is { } query && query(handle, out var value) == 0 ? value : null;

        ulong? Reasons(NvmlApi.GetULong? query) => query is not null && query(handle, out var value) == 0 ? value : null;

        double? Seconds(int index) => haveFields ? fields[index].UnsignedValue / 1_000_000_000d : null;

        uint? Throughput(int direction) => api.Get<NvmlApi.GetThroughput>("nvmlDeviceGetPcieThroughput") is { } query
            && query(handle, direction, out var value) == 0 ? value : null;
    }

    private ClockCapabilities ReadClockCapabilities(NvmlApi api, nint handle, string deviceId)
    {
        var timestamp = clock.GetTimestamp();

        if (_clockCapabilities is { } cached && cached.Handle == handle && cached.DeviceId == deviceId
            && clock.GetElapsedTime(cached.Timestamp, timestamp) < TimeSpan.FromSeconds(5))
        {
            return cached;
        }

        var getSupportedReasons = api.Get<NvmlApi.GetULong>("nvmlDeviceGetSupportedClocksEventReasons")
            ?? api.Get<NvmlApi.GetULong>("nvmlDeviceGetSupportedClocksThrottleReasons");

        ulong? supportedReasons = getSupportedReasons is not null && getSupportedReasons(handle, out var reasons) == 0
            ? reasons : null;

        _clockCapabilities = new ClockCapabilities(handle, deviceId, timestamp, supportedReasons, ReadSupportedStates(api, handle));
        return _clockCapabilities;
    }

    private sealed record ClockCapabilities(nint Handle, string DeviceId, long Timestamp, ulong? SupportedReasons, int[]? SupportedStates);

    private static int[]? ReadSupportedStates(NvmlApi api, nint handle)
    {
        var getStates = api.Get<NvmlApi.GetSupportedStates>("nvmlDeviceGetSupportedPerformanceStates");
        var states = new int[PStateCount];

        Array.Fill(states, UnknownPState);

        return getStates is not null && getStates(handle, states, PStateCount * sizeof(int)) == 0
            ? [.. states.Where(state => state is >= 0 and < PStateCount).Distinct().Order()]
            : null;
    }

    internal static nint? FindDevice(NvmlApi api, (uint Bus, uint Slot, uint DeviceId, uint SubsystemId) identity)
    {
        var getCount = api.Get<NvmlApi.GetCount>("nvmlDeviceGetCount_v2");
        var getHandle = api.Get<NvmlApi.GetHandle>("nvmlDeviceGetHandleByIndex_v2");
        var getPci = api.Get<NvmlApi.GetPciInfo>("nvmlDeviceGetPciInfo_v3");

        if (getCount is null || getHandle is null || getPci is null || getCount(out var count) != 0)
        {
            return null;
        }

        nint? match = null;

        for (uint index = 0; index < count; index++)
        {
            if (getHandle(index, out var device) != 0 || getPci(device, out var pci) != 0 || pci.Bus != identity.Bus
                || pci.Device != identity.Slot || pci.DeviceId != identity.DeviceId || pci.SubsystemId != identity.SubsystemId)
            {
                continue;
            }

            if (match.HasValue)
            {
                return null;
            }

            match = device;
        }

        return match;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _api?.Dispose();
            
            _api = null;
            _disposed = true;
        }
    }
}
