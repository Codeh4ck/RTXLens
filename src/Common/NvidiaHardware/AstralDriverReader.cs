using System.Runtime.InteropServices;

namespace RTXLens.Hardware.Nvidia;

// Read-only Windows x64 NVAPI interfaces. Lengths below are driver ABI sizes.
internal sealed unsafe class AstralDriverReader(NvidiaIdentityReader api)
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int QueryBlock(nint device, uint* buffer);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ReadI2c(nint device, ref I2cInfo info, ref uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct I2cInfo
    {
        public uint Version, DisplayMask;
        public byte DdcPort, Address;
        public byte* Register;
        public uint RegisterLength;
        public byte* Data;
        public uint Length, LegacySpeed, Speed;
        public byte Port;
        public uint HasPort;
    }

    private readonly QueryBlock? _clocks = api.Query<QueryBlock>(0xDCB616C3);
    private readonly QueryBlock? _voltage = api.Query<QueryBlock>(0x465F9BCF);
    private readonly QueryBlock? _thermal = api.Query<QueryBlock>(0x65FE3AAD);
    private readonly QueryBlock? _fans = api.Query<QueryBlock>(0x35AED5E8);
    private readonly ReadI2c? _pins = api.Query<ReadI2c>(0x4D7B0709);

    public List<NvidiaSensor> Read(nint handle)
    {
        var result = new List<NvidiaSensor>();
        Span<uint> clocks = stackalloc uint[2 + (2 * 32)];

        if (Query(_clocks, handle, clocks, 3))
        {
            AddClock("GPU Core", 0, clocks);
            AddClock("GPU Memory", 4, clocks);
        }

        Span<uint> voltage = stackalloc uint[0x4C / 4];

        if (Query(_voltage, handle, voltage, 1) && voltage[11] == 0)
        {
            result.Add(new("GPU Core Voltage", SensorType.Voltage, 0, voltage[10] / 1_000_000f));
        }

        // On Blackwell the thermal table's entries 1 and 2 are core and junction

        Span<uint> thermal = stackalloc uint[2 + 8 + 32];
        if (Query(_thermal, handle, thermal, 2, 7))
        {
            AddTemperature("GPU Core", thermal[11]);
            AddTemperature("GPU Memory Junction", thermal[12]);
        }

        // V1 status has a 40-byte header and 32 cooler records of 52 bytes.

        Span<uint> fans = stackalloc uint[10 + (32 * 13)];
        if (Query(_fans, handle, fans, 1) && fans[1] <= 32)
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
                result.Add(new(name, SensorType.Fan, index, cooler.Rpm));

                if (cooler.Duty <= 100)
                {
                    result.Add(new(name, SensorType.Control, index, cooler.Duty));
                }
            }
        }

        return result;

        void AddClock(string name, int domain, ReadOnlySpan<uint> values)
        {
            var offset = 2 + domain * 2;
            if ((values[offset] & 1) != 0)
            {
                result.Add(new(name, SensorType.Clock, domain, values[offset + 1] / 1000f));
            }
        }

        void AddTemperature(string name, uint raw)
        {
            var temperature = (int)raw / 256f;
            if (temperature is > 0 and < 160)
            {
                result.Add(new(name, SensorType.Temperature, 0, temperature));
            }
        }
    }

    public NvidiaSensor[] ReadPins(nint handle)
    {
        if (_pins is null || sizeof(I2cInfo) != 64)
        {
            return [];
        }

        byte register = 0x80;
        Span<byte> frame = stackalloc byte[24];

        frame.Fill(0xFF);

        fixed (byte* bytes = frame)
        {
            var info = new I2cInfo
            {
                Version = 0x30040, Address = 0x56, Register = &register, RegisterLength = 1,
                Data = bytes, Length = 24, LegacySpeed = 0xFFFF, Speed = 4, Port = 1, HasPort = 1
            };

            uint reserved = 0;
            return _pins(handle, ref info, ref reserved) == 0 && info.Length == 24 ? AstralBoard.DecodePins(frame) : [];
        }
    }

    private static bool Query(QueryBlock? read, nint handle, Span<uint> block, uint version, uint argument = 0)
    {
        if (read is null)
        {
            return false;
        }

        block.Clear();

        block[0] = (version << 16) | (uint)(block.Length * sizeof(uint));
        block[1] = argument;

        fixed (uint* data = block)
        {
            return read(handle, data) == 0;
        }
    }
}
