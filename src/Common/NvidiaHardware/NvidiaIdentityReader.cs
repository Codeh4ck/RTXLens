using LibreHardwareMonitor.Hardware;
using System.Runtime.InteropServices;
using System.Text;

namespace RTXLens.Hardware.Nvidia;

internal sealed class NvidiaIdentityReader : IDisposable
{
    private nint _library;
    private readonly Dictionary<string, nint> _handles = new(StringComparer.Ordinal);
    private readonly Dictionary<(string DeviceId, nint Handle, uint Domain), bool> _clockSupport = [];
    private MeasureClock? _measureClock;

    // Private NVAPI CLK_MEASURE_FREQ V1 - the request holds three 32-bit fields
    [StructLayout(LayoutKind.Sequential)]
    private struct ClockMeasurement
    {
        public uint Version;
        public uint DomainMask;
        public uint FrequencyKHz;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MeasureClock(nint handle, ref ClockMeasurement measurement);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint QueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Initialize();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Enumerate([Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 64)] nint[] handles, out uint count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetNumber(nint handle, out uint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetPciIds(nint handle, out uint deviceId, out uint subsystemId, out uint revisionId, out uint extendedId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetName(nint handle, [Out] StringBuilder name);

    public Dictionary<string, string> DiscoverDesktopIds()
    {
        _handles.Clear();

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!OperatingSystem.IsWindows() || (_library == 0 && !NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, "nvapi64.dll"), out _library)))
        {
            return result;
        }

        var query = Marshal.GetDelegateForFunctionPointer<QueryInterface>(NativeLibrary.GetExport(_library, "nvapi_QueryInterface"));
        var initialize = Get<Initialize>(0x0150E828);
        var enumerate = Get<Enumerate>(0xE5AC921F);
        var systemType = Get<GetNumber>(0xBAAABFCC);
        var bus = Get<GetNumber>(0x1BE0B8E5);
        var slot = Get<GetNumber>(0x2A0A350F);
        var pci = Get<GetPciIds>(0x2DDFB66E);
        var fullName = Get<GetName>(0xCEEE8E9F);

        if (initialize is null || initialize() != 0 || enumerate is null || systemType is null || bus is null || slot is null || pci is null || fullName is null)
        {
            return result;
        }

        _measureClock = Get<MeasureClock>(0x527FC458);

        var handles = new nint[64];
        if (enumerate(handles, out var count) != 0)
        {
            return result;
        }

        for (var index = 0; index < Math.Min(count, handles.Length); index++)
        {
            var handle = handles[index];
            var name = new StringBuilder(64);

            if (fullName(handle, name) != 0 || !name.ToString().Contains("GeForce RTX", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (systemType(handle, out var type) != 0 || type != 2
                || bus(handle, out var busId) != 0 || slot(handle, out var slotId) != 0
                || pci(handle, out var device, out var subsystem, out _, out _) != 0)
            {
                continue;
            }

            var id = $"nvidia-pci/{busId:x2}/{slotId:x2}/{device:x8}/{subsystem:x8}";
            result[$"/gpu-nvidia/{index}"] = id;

            if (!_handles.TryAdd(id, handle))
            {
                _handles[id] = 0;
            }
        }

        foreach (var key in _clockSupport.Keys.Where(key => _handles.GetValueOrDefault(key.DeviceId) != key.Handle).ToArray())
        {
            _clockSupport.Remove(key);
        }

        return result;

        T? Get<T>(uint id) where T : Delegate
        {
            var address = query(id);
            return address == 0 ? null : Marshal.GetDelegateForFunctionPointer<T>(address);
        }
    }

    public NvidiaSensor[] ReadDomainClocks(string deviceId)
    {
        if (_measureClock is not { } measure || !_handles.TryGetValue(deviceId, out var handle) || handle == 0)
        {
            return [];
        }

        var sensors = new List<NvidiaSensor>(2);

        Read(0x02, "GPU Crossbar");
        Read(0x04, "GPU System");

        return [.. sensors];

        void Read(uint mask, string name)
        {
            var key = (deviceId, handle, mask);
            var known = _clockSupport.TryGetValue(key, out var supported);

            if (known && !supported)
            {
                return;
            }

            var request = new ClockMeasurement { Version = 0x1000C, DomainMask = mask };
            var status = measure(handle, ref request);

            switch (status)
            {
                // NVAPI_NO_IMPLEMENTATION or NVAPI_NOT_SUPPORTED
                case -3 or -104:
                    _clockSupport[key] = false;
                    return;
                case 0 when request.FrequencyKHz is > 0 and < uint.MaxValue:
                    _clockSupport[key] = true;
                    sensors.Add(new NvidiaSensor(name, SensorType.Clock, 0, request.FrequencyKHz / 1000f));
                    break;
                default:
                    if (supported)
                    {
                        sensors.Add(new NvidiaSensor(name, SensorType.Clock, 0, null));
                    }
                    break;
            }
        }
    }

    public void ResetClocks()
    {
        _handles.Clear();
        _clockSupport.Clear();
    }

    public void Dispose()
    {
        if (_library != 0)
        {
            NativeLibrary.Free(_library);
            _library = 0;
        }
    }
}
