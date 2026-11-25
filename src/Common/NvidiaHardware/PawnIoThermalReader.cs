using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RTXLens.Hardware.Nvidia;

internal sealed unsafe class PawnIoThermalReader(TimeProvider clock) : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Open(out nint handle);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Load(nint handle, byte* blob, nuint length);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Ansi)]
    private delegate int Execute(nint handle, string name, ulong* input, nuint inputs, ulong* output, nuint outputs, out nuint written);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Close(nint handle);

    private nint _library;
    private nint _handle;
    private Execute? _execute;
    private Close? _close;
    private long? _lastAttempt;
    internal bool Enabled { get; } = IsEnabled();

    private static bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\RTXLens", "PawnIoThermals", 0) is 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public void Read(uint bus, uint device, uint function, List<NvidiaSensor> sensors)
    {
        if (!Enabled || !EnsureOpen())
        {
            return;
        }

        var address = stackalloc ulong[3] { bus, device, function };
        var output = stackalloc ulong[50];

        var hotspot = _execute!(_handle, "ioctl_read_thermal_registers", address, 3, output, 6, out var count) >= 0 && count == 6;
        if (hotspot && DecodeHotspot(output[0]) is { } degrees)
        {
            sensors.Add(new("GPU Hot Spot", SensorType.Temperature, 0, degrees));
        }

        var memory = _execute(_handle, "ioctl_read_memory_temperatures", address, 3, output, 50, out count) >= 0 && count == 50;
        if (memory)
        {
            AddMemoryTemperatures(new ReadOnlySpan<ulong>(output, 50), sensors);
        }

        if (!hotspot && !memory)
        {
            Dispose();
            _lastAttempt = clock.GetTimestamp();
        }
    }

    internal static float? DecodeHotspot(ulong raw)
    {
        var degrees = (raw & 0xffff) / 256f;
        return (raw & (1ul << 30)) != 0 && degrees is > 0 and < 160 ? degrees : null;
    }

    internal static void AddMemoryTemperatures(ReadOnlySpan<ulong> raw, List<NvidiaSensor> sensors)
    {
        if (raw.Length < 2 || raw[1] > 48 || raw[1] > (ulong)(raw.Length - 2))
        {
            return;
        }

        for (var i = 0; i < (int)raw[1]; i++)
        {
            var degrees = unchecked((int)raw[2 + i]);
            if (degrees is >= -40 and < 160)
            {
                sensors.Add(new("GPU Memory Chip", SensorType.Temperature, i + 1, degrees));
            }
        }
    }

    private bool EnsureOpen()
    {
        if (_handle != 0)
        {
            return true;
        }

        if (!OperatingSystem.IsWindows() || (_lastAttempt is { } last && clock.GetElapsedTime(last) < TimeSpan.FromSeconds(30)))
        {
            return false;
        }

        _lastAttempt = clock.GetTimestamp();

        var module = Path.Combine(AppContext.BaseDirectory, "PawnIO", "Nvidia.bin");
        var library = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", "PawnIOLib.dll");
        var ready = false;

        try
        {
            if (!File.Exists(module) || !NativeLibrary.TryLoad(library, out _library))
            {
                return false;
            }

            var open = Export<Open>("pawnio_open");
            var load = Export<Load>("pawnio_load");

            _close = Export<Close>("pawnio_close");
            _execute = Export<Execute>("pawnio_execute");

            var blob = File.ReadAllBytes(module);
            if (open(out var handle) >= 0 && handle != 0 && handle != -1)
            {
                _handle = handle;
                fixed (byte* bytes = blob)
                {
                    ready = load(_handle, bytes, (nuint)blob.Length) >= 0;
                }
            }
        }
        catch (Exception error) when (error is EntryPointNotFoundException or BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (!ready)
            {
                Dispose();
                _lastAttempt = clock.GetTimestamp();
            }
        }
        return ready;
    }

    private T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

    public void Dispose()
    {
        if (_handle != 0)
        {
            _close!(_handle);
            _handle = 0;
        }
        if (_library != 0)
        {
            NativeLibrary.Free(_library);
            _library = 0;
        }
        _lastAttempt = null;
    }
}
