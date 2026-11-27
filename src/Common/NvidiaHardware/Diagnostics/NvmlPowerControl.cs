using System.Runtime.InteropServices;
using RTXLens.Plugins.GenericNvidia.Contracts.Devices;

namespace RTXLens.Hardware.Nvidia.Diagnostics;

public sealed class NvmlPowerControl : IDisposable
{
    private readonly NvmlApi _api;
    private readonly nint _device;
    private readonly SetLimit _set;
    private readonly NvmlApi.GetPowerRange? _range;
    private readonly NvmlApi.GetFields? _fields;
    private readonly NvmlApi.GetUInt? _enforced;
    private readonly NvmlApi.GetUInt? _configured;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetLimit(nint device, uint milliwatts);

    private NvmlPowerControl(NvmlApi api, nint device, SetLimit set)
    {
        _api = api;
        _device = device;
        _set = set;
        _range = api.Get<NvmlApi.GetPowerRange>("nvmlDeviceGetPowerManagementLimitConstraints");
        _fields = api.Get<NvmlApi.GetFields>("nvmlDeviceGetFieldValues");
        _enforced = api.Get<NvmlApi.GetUInt>("nvmlDeviceGetEnforcedPowerLimit");
        _configured = api.Get<NvmlApi.GetUInt>("nvmlDeviceGetPowerManagementLimit");
    }

    public static NvmlPowerControl Open(string gpuId)
    {
        if (!NvidiaPciIdentity.TryParse(gpuId, out var identity))
        {
            throw new ArgumentException("Invalid target GPU.");
        }

        var api = NvmlApi.TryOpen() ?? throw new InvalidOperationException("NVML is unavailable.");

        try
        {
            var device = NvidiaDiagnosticsReader.FindDevice(api, identity, out _) ?? throw new InvalidOperationException("The selected GPU is unavailable.");
            var set = api.Get<SetLimit>("nvmlDeviceSetPowerManagementLimit") ?? throw new InvalidOperationException("The driver cannot set a power limit.");

            return new(api, device, set);
        }
        catch
        {
            api.Dispose();
            throw;
        }
    }

    public (uint Minimum, uint Maximum, uint Requested, uint? Enforced) Read()
    {
        if (_range is null || _range(_device, out var min, out var max) != 0 || min == 0 || max < min)
        {
            throw new InvalidOperationException("The driver did not report a usable power-limit range.");
        }

        // Prefer the getter paired with the setter. Use the GPU-scoped requested-limit
        // field on drivers without that interface. Neither the default nor the enforced
        // limit is a substitute for the owner's configured setting.
        if (_configured is null || _configured(_device, out var value) != 0 || value < min || value > max)
        {
            NvmlApi.FieldValue[] fields = [new() { Id = 192 }];

            if (_fields is null || _fields(_device, 1, fields) != 0 || fields[0].UnsignedValue is not { } requested || requested < min || requested > max)
            {
                throw new InvalidOperationException("The driver did not report a usable configured power limit.");
            }

            value = (uint)requested;
        }

        uint? enforced = _enforced is not null && _enforced(_device, out var effective) == 0 && effective > 0 ? effective : null;
        return (min, max, value, enforced);
    }

    public void Change(uint expected, uint target)
    {
        var limits = Read();
        if (limits.Requested != expected || target < limits.Minimum || target > limits.Maximum)
        {
            throw new InvalidOperationException("The power limit or the supported range changed before the write.");
        }

        var status = _set(_device, target);
        if (status != 0)
        {
            throw new InvalidOperationException($"NVIDIA rejected the power limit (NVML status {status}).");
        }
    }

    public void Dispose() => _api.Dispose();
}
