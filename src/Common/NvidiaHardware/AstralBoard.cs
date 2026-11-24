using System.Buffers.Binary;
using RTXLens.Plugins.GenericNvidia.Contracts.Devices;

namespace RTXLens.Hardware.Nvidia;

public static class AstralBoard
{
    public static bool IsSupported(uint subsystemId) => NvidiaPciIdentity.IsAstral(subsystemId);

    public static bool HasAlternativeInput(uint id) => id is 0x8A5A1043 or 0x8A3C1043 or 0x8A611043;

    internal static NvidiaSensor[] DecodePins(ReadOnlySpan<byte> frame)
    {
        if (frame.Length != 24)
        {
            return [];
        }

        var readings = new List<NvidiaSensor>(20);
        float totalAmps = 0, totalWatts = 0;

        for (var pin = 1; pin <= 6; pin++)
        {
            var pair = frame[((6 - pin) * 4)..];
            var millivolts = BinaryPrimitives.ReadUInt16BigEndian(pair);
            var milliamps = BinaryPrimitives.ReadUInt16BigEndian(pair[2..]);

            if (millivolts > 16000 || milliamps == ushort.MaxValue || (millivolts == 0 && milliamps != 0))
            {
                return [];
            }

            var name = $"12VHPWR Pin {pin}";
            var volts = millivolts / 1000f;
            var amps = milliamps / 1000f;

            readings.Add(new(name, SensorType.Voltage, pin, volts));
            readings.Add(new(name, SensorType.Current, pin, amps));
            readings.Add(new(name, SensorType.Power, pin, volts * amps));

            totalAmps += amps;
            totalWatts += volts * amps;
        }

        // A disconnected or uninitialized block can return 24 zero bytes.
        if (frame.IndexOfAnyExcept((byte)0) < 0)
        {
            return [];
        }

        readings.Add(new("12VHPWR Connector", SensorType.Current, 1, totalAmps));
        readings.Add(new("12VHPWR Connector", SensorType.Power, 1, totalWatts));

        return [.. readings];
    }
}
