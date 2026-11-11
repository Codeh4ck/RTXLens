using System.Runtime.InteropServices;

namespace RTXLens.Plugins.GenericNvidia.Diagnostics;

internal sealed class NvmlApi : IDisposable
{
    private readonly nint _library;
    private readonly Initialize _shutdown;

    private NvmlApi(nint library, Initialize shutdown)
    {
        _library = library;
        _shutdown = shutdown;
    }

    internal static NvmlApi? TryOpen()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
        {
            return null;
        }

        var libraryPath = Path.Combine(Environment.SystemDirectory, "nvml.dll");
        if (!NativeLibrary.TryLoad(libraryPath, out var library))
        {
            libraryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "NVIDIA Corporation", "NVSMI", "nvml.dll");
            if (!NativeLibrary.TryLoad(libraryPath, out library))
            {
                return null;
            }
        }

        var initialize = Export<Initialize>(library, "nvmlInit_v2");
        var shutdown = Export<Initialize>(library, "nvmlShutdown");
        if (initialize is not null && shutdown is not null && initialize() == 0)
        {
            return new NvmlApi(library, shutdown);
        }

        NativeLibrary.Free(library);
        return null;
    }

    internal T? Get<T>(string symbol) where T : Delegate => Export<T>(_library, symbol);

    private static T? Export<T>(nint library, string symbol) where T : Delegate =>
        NativeLibrary.TryGetExport(library, symbol, out var address)
            ? Marshal.GetDelegateForFunctionPointer<T>(address)
            : null;

    public void Dispose()
    {
        _shutdown();
        NativeLibrary.Free(_library);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Initialize();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int GetCount(out uint count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int GetHandle(uint index, out nint device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int GetPciInfo(nint device, out PciInfo pci);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int GetUInt(nint device, out uint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int GetULong(nint device, out ulong value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int GetThroughput(nint device, int direction, out uint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int GetSupportedStates(nint device, [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 16)] int[] states, uint sizeBytes);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int GetFields(nint device, int count,
        [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] FieldValue[] values);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int GetPowerRange(nint device, out uint minimum, out uint maximum);

    // nvmlPciInfo_t has a 16-byte legacy bus ID and a 32-byte bus ID after the numeric fields.
    [StructLayout(LayoutKind.Explicit, Size = 68)]
    internal struct PciInfo
    {
        [FieldOffset(20)] public uint Bus;
        [FieldOffset(24)] public uint Device;
        [FieldOffset(28)] public uint DeviceId;
        [FieldOffset(32)] public uint SubsystemId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FieldValue
    {
        public uint Id;
        public uint ScopeId;
        public long Timestamp;
        public long LatencyMicroseconds;
        public int ValueType;
        public int Result;
        public ulong Value;

        public readonly ulong? UnsignedValue => Result != 0 ? null : ValueType switch
        {
            1 or 2 => (uint)Value, // On Windows C unsigned long is 32 bits.
            3 => Value,
            _ => null
        };
    }
}
