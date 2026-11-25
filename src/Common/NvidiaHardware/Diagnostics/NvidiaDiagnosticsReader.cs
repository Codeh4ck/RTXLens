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
    private NvidiaQuerySupport<(nint Device, string Query, int Argument)> _support = new();
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

            if (Open() is not { } api || FindDevice(api, identity, out _) is not { } handle)
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

    internal List<NvidiaSensor> ReadBoard(string deviceId, out NvmlApi.PciInfo selectedPci)
    {
        selectedPci = default;
        if (!NvidiaPciIdentity.TryParse(deviceId, out var identity))
        {
            return [];
        }

        lock (_gate)
        {
            if (Open() is not { } api || FindDevice(api, identity, out selectedPci) is not { } handle)
            {
                return [];
            }

            var sensors = new List<NvidiaSensor>();
            var power = api.Get<NvmlApi.GetUInt>("nvmlDeviceGetPowerUsage");

            if (power is not null && _support.CanQuery((handle, "nvmlDeviceGetPowerUsage", 0))
                && _support.Nvml((handle, "nvmlDeviceGetPowerUsage", 0), power(handle, out var milliwatts)))
            {
                sensors.Add(new("GPU Package", SensorType.Power, 0, milliwatts / 1000f));
            }

            var utilization = api.Get<NvmlApi.GetUtilization>("nvmlDeviceGetUtilizationRates");
            if (utilization is not null && _support.CanQuery((handle, "nvmlDeviceGetUtilizationRates", 0))
                                        && _support.Nvml((handle, "nvmlDeviceGetUtilizationRates", 0), utilization(handle, out var rates)) && rates.Gpu <= 100)
            {
                sensors.Add(new("GPU Core", SensorType.Load, 0, rates.Gpu));
            }

            var memory = api.Get<NvmlApi.GetMemory>("nvmlDeviceGetMemoryInfo");
            if (memory is not null && _support.CanQuery((handle, "nvmlDeviceGetMemoryInfo", 0))
                && _support.Nvml((handle, "nvmlDeviceGetMemoryInfo", 0), memory(handle, out var vram))
                && vram.Total > 0)
            {
                const float mib = 1024 * 1024;

                sensors.Add(new("GPU Memory Total", SensorType.SmallData, 0, vram.Total / mib));
                sensors.Add(new("GPU Memory Used", SensorType.SmallData, 0, vram.Used / mib));
                sensors.Add(new("GPU Memory Free", SensorType.SmallData, 0, vram.Free / mib));
                sensors.Add(new("GPU Memory", SensorType.Load, 0, 100f * vram.Used / vram.Total));
            }

            return sensors;
        }
    }

    internal void Reset()
    {
        lock (_gate)
        {
            _api?.Dispose();

            _api = null;
            _support = new();
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

        uint[] fieldIds = [PowerCapCounter, SoftwareThermalCounter, HardwareThermalCounter, HardwarePowerBrakeCounter, SyncBoostCounter, ReplayRolloverCounter];

        var fields = fieldIds
            .Where(id => _support.CanQuery((handle, "field", (int)id)))
            .Select(id => new NvmlApi.FieldValue { Id = id })
            .ToArray();

        var getFields = api.Get<NvmlApi.GetFields>("nvmlDeviceGetFieldValues");
        var haveFields = fields.Length > 0 && getFields is not null && _support.CanQuery((handle, "fields", 0))
            && _support.Nvml((handle, "fields", 0), getFields(handle, fields.Length, fields));

        if (haveFields)
        {
            foreach (var field in fields)
            {
                _support.Nvml((handle, "field", (int)field.Id), field.Result);
            }
        }

        var limits = new NvidiaClockLimitDurations(Seconds(PowerCapCounter), Seconds(SoftwareThermalCounter),
            Seconds(HardwareThermalCounter), Seconds(HardwarePowerBrakeCounter), Seconds(SyncBoostCounter));

        var rollover = Field(ReplayRolloverCounter);
        var pcie = new NvidiaPcieDiagnostics(
            Query("nvmlDeviceGetCurrPcieLinkGeneration"),
            Query("nvmlDeviceGetMaxPcieLinkGeneration"),
            Query("nvmlDeviceGetGpuMaxPcieLinkGeneration"),
            Query("nvmlDeviceGetCurrPcieLinkWidth"),
            Query("nvmlDeviceGetMaxPcieLinkWidth"),
            Query("nvmlDeviceGetPcieSpeed"),
            Query("nvmlDeviceGetPcieLinkMaxSpeed") switch
            {
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
        var haveRange = getRange is not null && _support.CanQuery((handle, "nvmlDeviceGetPowerManagementLimitConstraints", 0))
                && _support.Nvml((handle, "nvmlDeviceGetPowerManagementLimitConstraints", 0), getRange(handle, out minimum, out maximum));

        var powerLimits = new NvidiaPowerLimits(Query("nvmlDeviceGetPowerManagementDefaultLimit") / 1000d,
            Query("nvmlDeviceGetEnforcedPowerLimit") / 1000d, haveRange ? minimum / 1000d : null,
            haveRange ? maximum / 1000d : null);

        return new NvidiaDiagnostics(deviceId, clock.GetUtcNow(), Reasons(activeReasons), clockCapabilities.SupportedReasons, limits,
            currentState, clockCapabilities.SupportedStates, pcie, powerLimits);

        uint? Query(string symbol) => _support.CanQuery((handle, symbol, 0)) && api.Get<NvmlApi.GetUInt>(symbol) is { } query
            && _support.Nvml((handle, symbol, 0), query(handle, out var value)) ? value : null;

        ulong? Reasons(NvmlApi.GetULong? query) => query is not null && _support.CanQuery((handle, "activeReasons", 0))
            && _support.Nvml((handle, "activeReasons", 0), query(handle, out var value)) ? value : null;

        ulong? Field(uint id) => haveFields && Array.FindIndex(fields, item => item.Id == id) is >= 0 and var index
            ? fields[index].UnsignedValue : null;

        double? Seconds(uint id) => Field(id) / 1_000_000_000d;

        uint? Throughput(int direction) => api.Get<NvmlApi.GetThroughput>("nvmlDeviceGetPcieThroughput") is { } query
            && _support.CanQuery((handle, "throughput", direction))
            && _support.Nvml((handle, "throughput", direction), query(handle, direction, out var value)) ? value : null;
    }

    private ClockCapabilities ReadClockCapabilities(NvmlApi api, nint handle, string deviceId)
    {
        var timestamp = clock.GetTimestamp();

        if (_clockCapabilities is { } cached && cached.Handle == handle && cached.DeviceId == deviceId
            && (cached.SupportedReasons.HasValue || !_support.CanQuery((handle, "supportedReasons", 0)))
            && (cached.SupportedStates is not null || !_support.CanQuery((handle, "nvmlDeviceGetSupportedPerformanceStates", 0)))
            && clock.GetElapsedTime(cached.Timestamp, timestamp) < TimeSpan.FromSeconds(5))
        {
            return cached;
        }

        var getSupportedReasons = api.Get<NvmlApi.GetULong>("nvmlDeviceGetSupportedClocksEventReasons")
            ?? api.Get<NvmlApi.GetULong>("nvmlDeviceGetSupportedClocksThrottleReasons");

        ulong? supportedReasons = getSupportedReasons is not null && _support.CanQuery((handle, "supportedReasons", 0))
            && _support.Nvml((handle, "supportedReasons", 0), getSupportedReasons(handle, out var reasons))
            ? reasons : null;

        _clockCapabilities = new ClockCapabilities(handle, deviceId, timestamp, supportedReasons, ReadSupportedStates(api, handle));
        return _clockCapabilities;
    }

    private sealed record ClockCapabilities(nint Handle, string DeviceId, long Timestamp, ulong? SupportedReasons, int[]? SupportedStates);

    private int[]? ReadSupportedStates(NvmlApi api, nint handle)
    {
        var getStates = api.Get<NvmlApi.GetSupportedStates>("nvmlDeviceGetSupportedPerformanceStates");
        var states = new int[PStateCount];

        Array.Fill(states, UnknownPState);

        return getStates is not null && _support.CanQuery((handle, "nvmlDeviceGetSupportedPerformanceStates", 0))
                && _support.Nvml((handle, "nvmlDeviceGetSupportedPerformanceStates", 0), getStates(handle, states, PStateCount * sizeof(int)))
            ? [.. states.Where(state => state is >= 0 and < PStateCount).Distinct().Order()]
            : null;
    }

    internal static nint? FindDevice(NvmlApi api, (uint Bus, uint Slot, uint DeviceId, uint SubsystemId) identity, out NvmlApi.PciInfo selectedPci)
    {
        selectedPci = default;
        var getCount = api.Get<NvmlApi.GetCount>("nvmlDeviceGetCount_v2");
        var getHandle = api.Get<NvmlApi.GetHandle>("nvmlDeviceGetHandleByIndex_v2");
        var getPci = api.Get<NvmlApi.GetPciInfo>("nvmlDeviceGetPciInfo_v3");

        if (getCount is null || getHandle is null || getPci is null || NvidiaDriverStatus.Nvml(getCount(out var count)) != 0)
        {
            return null;
        }

        nint? match = null;
        var lostStatus = 0;

        for (uint index = 0; index < count; index++)
        {
            var status = getHandle(index, out var device);
            NvmlApi.PciInfo pci = default;

            if (status == 0)
            {
                status = getPci(device, out pci);
            }

            if (NvidiaDriverStatus.NvmlSessionLost(status))
            {
                lostStatus = status;
            }

            if (status != 0 || pci.Bus != identity.Bus || pci.Device != identity.Slot || pci.DeviceId != identity.DeviceId || pci.SubsystemId != identity.SubsystemId)
            {
                continue;
            }

            if (match.HasValue)
            {
                selectedPci = default;
                return null;
            }

            selectedPci = pci;
            match = device;
        }

        // Another GPU can be offline while the selected GPU still answers.
        if (!match.HasValue)
        {
            NvidiaDriverStatus.Nvml(lostStatus);
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
