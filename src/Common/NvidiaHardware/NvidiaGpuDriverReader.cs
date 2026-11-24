using System.Runtime.InteropServices;

namespace RTXLens.Hardware.Nvidia;

// Read-only Windows x64 NVAPI interfaces. Lengths below are driver ABI sizes.
internal sealed unsafe class NvidiaGpuDriverReader(NvidiaIdentityReader api)
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int QueryBlock(nint device, uint* buffer);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int QueryTarget(nint device, uint target, uint* settings);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetTachometer(nint device, out uint rpm);

    private readonly QueryBlock? _clocks = api.Query<QueryBlock>(0xDCB616C3);
    private readonly QueryBlock? _voltage = api.Query<QueryBlock>(0x465F9BCF);
    private readonly QueryBlock? _thermal = api.Query<QueryBlock>(0x65FE3AAD);
    private readonly QueryBlock? _fans = api.Query<QueryBlock>(0x35AED5E8);
    private readonly QueryTarget? _thermalSettings = api.Query<QueryTarget>(0xE3640A56);
    private readonly QueryTarget? _coolerSettings = api.Query<QueryTarget>(0xDA141340);
    private readonly GetTachometer? _tachometer = api.Query<GetTachometer>(0x5F608315);
    private readonly QueryBlock? _pstates = api.Query<QueryBlock>(0x60DED2ED);
    private readonly QueryBlock? _memory = api.Query<QueryBlock>(0x07F9B368);

    private readonly NvidiaQuerySupport<(nint, Delegate, uint, uint)> _support = new();

    public void Read(nint handle, string model, List<NvidiaSensor> result)
    {
        Span<uint> clocks = stackalloc uint[2 + (2 * 32)];

        if (Query(_clocks, handle, clocks, 3) || Query(_clocks, handle, clocks, 2) || Query(_clocks, handle, clocks, 1))
        {
            AddClock("GPU Core", 0, clocks);
            AddClock("GPU Memory", 4, clocks);
        }

        Span<uint> voltage = stackalloc uint[0x4C / 4];

        if (Query(_voltage, handle, voltage, 1) && voltage[11] == 0 && voltage[10] is > 0 and < 3_000_000)
        {
            result.Add(new("GPU Core Voltage", SensorType.Voltage, 0, voltage[10] / 1_000_000f));
        }

        ReadTemperatures(handle, model, result);

        Span<uint> fans = stackalloc uint[10 + (32 * 13)];
        if (Query(_fans, handle, fans, 1) && fans[1] is > 0 and <= 32)
        {
            var coolers = new List<(uint Id, uint Rpm, uint Duty)>();
            for (var i = 0; i < fans[1]; i++)
            {
                var item = fans[(10 + 13 * i)..];
                coolers.Add((item[0], item[1], item[4]));
            }

            var index = 0;

            foreach (var cooler in coolers.OrderBy(item => item.Id))
            {
                var name = $"GPU Fan {++index}";
                result.Add(new(name, SensorType.Fan, index, cooler.Rpm == uint.MaxValue ? null : cooler.Rpm));

                if (cooler.Duty <= 100)
                {
                    result.Add(new(name, SensorType.Control, index, cooler.Duty));
                }
            }
        }
        else if (_tachometer is not null && _support.CanQuery((handle, _tachometer, 0, 0))
                 && _support.NvApi((handle, _tachometer, 0, 0), _tachometer(handle, out var rpm)) && rpm != uint.MaxValue)
        {
            result.Add(new("GPU Fan 1", SensorType.Fan, 1, rpm));

            // The tachometer supplies one RPM without a cooler ID. Pair duty only for a single cooler.
            Span<uint> settings = stackalloc uint[2 + 20 * 12];
            if (QuerySettings(_coolerSettings, handle, 7, settings) && settings[1] == 1 && settings[8] <= 100)
            {
                result.Add(new("GPU Fan 1", SensorType.Control, 1, settings[8]));
            }
        }

        AddMissingLoadAndMemory(handle, result);
        return;

        void AddClock(string name, int domain, ReadOnlySpan<uint> values)
        {
            var offset = 2 + (domain * 2);
            if ((values[offset] & 1) != 0 && values[offset + 1] is > 0 and < uint.MaxValue)
            {
                result.Add(new(name, SensorType.Clock, domain, values[offset + 1] / 1000f));
            }
        }
    }

    private void ReadTemperatures(nint handle, string model, List<NvidiaSensor> result)
    {
        if (ThermalLayout(model) is { } layout)
        {
            Span<uint> thermal = stackalloc uint[2 + 8 + 32];
            var mask = (1u << (layout.Junction + 1)) - 1;

            if (Query(_thermal, handle, thermal, 2, mask))
            {
                AddExtendedTemperatures(result, layout, thermal[10..]);
            }
            else if (Query(_thermal, handle, thermal, 2, 2))
            {
                AddExtendedTemperatures(result, layout, thermal.Slice(10, 2));
            }
        }

        if (_thermalSettings is null || (result.Any(sensor => sensor is { Type: SensorType.Temperature, Name: "GPU Core" })
                                         && result.Any(sensor => sensor.Name == "GPU Memory Junction")))
        {
            return;
        }

        Span<uint> settings = stackalloc uint[2 + (3 * 5)];
        if (!QuerySettings(_thermalSettings, handle, 15, settings) || settings[1] > 3)
        {
            return;
        }

        for (var i = 0; i < settings[1]; i++)
        {
            var entry = settings[(2 + (5 * i))..];
            var name = entry[4] switch { 1 => "GPU Core", 2 => "GPU Memory", _ => null };
            if (name is not null && !result.Any(sensor => sensor.Type == SensorType.Temperature && sensor.Name == name))
            {
                AddTemperature(result, name, (int)entry[3]);
            }
        }
    }

    internal static void AddExtendedTemperatures(List<NvidiaSensor> result, (bool Blackwell, int Junction) layout, ReadOnlySpan<uint> channels)
    {
        AddTemperature(result, layout.Blackwell ? "GPU Core" : "GPU Hot Spot", (int)channels[1] / 256f);
        if (layout.Junction < channels.Length)
        {
            AddTemperature(result, "GPU Memory Junction", (int)channels[layout.Junction] / 256f);
        }
    }

    internal static (bool Blackwell, int Junction)? ThermalLayout(string model)
    {
        if (model.Contains("GeForce RTX 50", StringComparison.OrdinalIgnoreCase))
        {
            return (true, 2);
        }

        if (model.Contains("GeForce RTX 40", StringComparison.OrdinalIgnoreCase))
        {
            return (false, 7);
        }

        if (model.Contains("GeForce RTX 30", StringComparison.OrdinalIgnoreCase) || model.Contains("GeForce RTX 20", StringComparison.OrdinalIgnoreCase))
        {
            return (false, 9);
        }

        return null;
    }

    private static void AddTemperature(List<NvidiaSensor> result, string name, float degrees)
    {
        if (degrees is > 0 and < 160)
        {
            result.Add(new(name, SensorType.Temperature, 0, degrees));
        }
    }

    private void AddMissingLoadAndMemory(nint handle, List<NvidiaSensor> result)
    {
        if (!result.Any(sensor => sensor is { Type: SensorType.Load, Name: "GPU Core" }))
        {
            Span<uint> load = stackalloc uint[2 + 8 * 2];
            if (Query(_pstates, handle, load, 1) && (load[2] & 1) != 0 && load[3] <= 100)
            {
                result.Add(new("GPU Core", SensorType.Load, 0, load[3]));
            }
        }

        if (result.All(sensor => sensor.Name != "GPU Memory Total"))
        {
            Span<uint> memory = stackalloc uint[6];
            if (Query(_memory, handle, memory, 2) && memory[1] > 0 && memory[5] <= memory[1])
            {
                var used = memory[1] - memory[5];
                result.Add(new("GPU Memory Total", SensorType.SmallData, 0, memory[1] / 1024f));
                result.Add(new("GPU Memory Used", SensorType.SmallData, 0, used / 1024f));
                result.Add(new("GPU Memory Free", SensorType.SmallData, 0, memory[5] / 1024f));
                result.Add(new("GPU Memory", SensorType.Load, 0, 100f * used / memory[1]));
            }
        }
    }

    private bool QuerySettings(QueryTarget? read, nint handle, uint target, Span<uint> settings)
    {
        if (read is null || !_support.CanQuery((handle, read, 2, target)))
        {
            return false;
        }

        settings.Clear();
        settings[0] = (2u << 16) | (uint)(settings.Length * sizeof(uint));
        fixed (uint* data = settings)
        {
            return _support.NvApi((handle, read, 2, target), read(handle, target, data));
        }
    }

    private bool Query(QueryBlock? read, nint handle, Span<uint> block, uint version, uint argument = 0)
    {
        if (read is null || !_support.CanQuery((handle, read, version, argument)))
        {
            return false;
        }

        block.Clear();

        block[0] = (version << 16) | (uint)(block.Length * sizeof(uint));
        block[1] = argument;

        fixed (uint* data = block)
        {
            return _support.NvApi((handle, read, version, argument), read(handle, data));
        }
    }
}
