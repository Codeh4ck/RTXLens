using System.Runtime.InteropServices;

namespace RTXLens.Hardware.Nvidia;

// Read-only Windows x64 NVAPI interfaces. Lengths below are driver ABI sizes.
internal sealed unsafe class AstralDriverReader(NvidiaIdentityReader api)
{
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

    private readonly ReadI2c? _pins = api.Query<ReadI2c>(0x4D7B0709);

    private readonly NvidiaQuerySupport<nint> _support = new();

    public NvidiaSensor[] ReadPins(nint handle)
    {
        if (_pins is null || sizeof(I2cInfo) != 64 || !_support.CanQuery(handle))
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
            return _support.NvApi(handle, _pins(handle, ref info, ref reserved)) && info.Length == 24 ? AstralBoard.DecodePins(frame) : [];
        }
    }
}
