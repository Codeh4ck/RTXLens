using System.Buffers.Binary;
using System.Text;
using RTXLens.Plugins.WireView.Device;
using RTXLens.Tests.Infrastructure;

namespace RTXLens.Tests.Core;

internal static class WireViewConfigParserTests
{
    public static void Run(TestRunner runner)
    {
        runner.Run(ParsesReportedTotalsAndVoltageFromTheSensorPacket);
        runner.Run(ParsesVersionZeroWithoutAveraging);
        runner.Run(ParsesVersionOneAveraging);
        runner.Run(ParsesVersionTwoAndPreservesThresholdsAndMasks);
        runner.Run(EditsAllFullLayoutsWithoutOverwritingOtherBytes);
        runner.Run(RejectsOutOfRangeAndUnsupportedConfigurationChanges);
        runner.Run(SavesOnlyAfterReadBackAndVerifiesReloadedSettings);
    }

    private static void ParsesReportedTotalsAndVoltageFromTheSensorPacket()
    {
        var bytes = new byte[100];
        for (var pin = 0; pin < 6; pin++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(12 + pin * 12), 12000);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16 + pin * 12), 1000);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(84), 62345);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(88), 5000);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(92), 12073);
        var data = WireViewProReader.Parse(bytes);

        TestAssert.Equal<double?>(12.073, data.ReportedAverageVoltageV);
        TestAssert.Equal(62.345, data.TotalPowerW);
        TestAssert.Equal(5d, data.TotalCurrentA);
        TestAssert.Equal(72d, data.PinPower.Sum());
        TestAssert.Equal(6d, data.PinCurrent.Sum());
    }

    private static void ParsesVersionZeroWithoutAveraging()
    {
        var bytes = CreateConfigBytes(64);
        var configuration = WireViewProReader.ParseConfig(bytes, version: 0);

        TestAssert.Equal("Test device", configuration.FriendlyName);
        TestAssert.Equal<string?>(null, configuration.Averaging);
        TestAssert.Equal(0, configuration.Version);
    }

    private static void ParsesVersionOneAveraging()
    {
        var bytes = CreateConfigBytes(72);
        bytes[64] = 5;

        var configuration = WireViewProReader.ParseConfig(bytes, version: 1);

        TestAssert.Equal("709ms", configuration.Averaging);
        TestAssert.Equal(1, configuration.Version);
    }

    private static void ParsesVersionTwoAndPreservesThresholdsAndMasks()
    {
        var bytes = CreateConfigBytes(88);
        bytes[64] = 6;

        var configuration = WireViewProReader.ParseConfig(bytes, version: 2);

        TestAssert.Equal(2, configuration.Version);
        TestAssert.Equal("1417ms", configuration.Averaging);
        TestAssert.Equal("Fixed", configuration.FanMode);
        TestAssert.Equal("Tmax", configuration.FanTempSource);
        TestAssert.Equal(35d, configuration.FanTempMinC);
        TestAssert.Equal(80d, configuration.TsFaultThresholdC);
        TestAssert.Equal(9.2, configuration.WireOcpFaultThresholdA);
        TestAssert.Equal((ushort)600, configuration.OppFaultThresholdW);
        TestAssert.Equal((ushort)0x3E, configuration.FaultDisplayEnable);
        TestAssert.Equal((ushort)0x08, configuration.FaultHardPowerEnable);
        TestAssert.Equal((byte)3, configuration.ShutdownWaitTimeS);
    }

    private static void EditsAllFullLayoutsWithoutOverwritingOtherBytes()
    {
        foreach (var (version, size, offset) in new[] { (0, 72, 64), (1, 74, 65), (2, 96, 69) })
        {
            var bytes = CreateConfigBytes(size);
            bytes[2] = (byte)version;
            bytes[0] = 0xAB;
            bytes[1] = 0xCD;
            bytes[35] = 0xFA;
            bytes[45] = 0xFB;
            bytes[47] = 0x80;

            if (version > 0)
            {
                bytes[64] = 8;
            }

            if (version == 2)
            {
                bytes[95] = 0xFC;
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76), 0xF1020304);
            }

            var current = WireViewProReader.ParseConfig(bytes, version);
            var changed = current with
            {
                BacklightDuty = 75,
                Display = current.Display! with { CurrentScale = 3 }
            };
            var expected = (byte[])bytes.Clone();
            expected[44] = 75;
            expected[offset] = 3;

            var actual = WireViewConfigCodec.Patch(bytes, changed, firmwareVersion: 5);
            TestAssert.True(expected.SequenceEqual(actual), $"Version {version} must preserve all other bytes.");
            TestAssert.True(bytes.SequenceEqual(WireViewConfigCodec.Patch(bytes, current, 5)), "An unchanged edit must keep every byte.");
            TestAssert.Equal(version == 0 ? null : "5668ms", current.Averaging);
        }
    }

    private static void RejectsOutOfRangeAndUnsupportedConfigurationChanges()
    {
        var bytes = CreateConfigBytes(96);
        bytes[2] = 2;
        bytes[64] = 6;
        var current = WireViewProReader.ParseConfig(bytes, 2);
        var display = current.Display!;
        var original = (byte[])bytes.Clone();

        foreach (var invalid in new[]
        {
            current with { FriendlyName = "Σ" },
            current with { BacklightDuty = 101 },
            current with { WireOcpFaultThresholdA = 9.25 },
            current with { WireOcpFaultThresholdA = 25.6 },
            current with { WireOcpFaultThresholdA = double.NaN },
            current with { TsFaultThresholdC = double.PositiveInfinity },
            current with { FanMode = "Curve", FanTempMaxC = current.FanTempMinC },
            current with { CurrentImbalanceThreshold = 101 },
            current with { FaultBuzzerEnable = 0x8000 },
            current with { Averaging = "2834ms" },
            current with { Display = display with { PrimaryColor = null } },
            current with { Display = display with { BackgroundBitmapId = 12 } },
            current with { Display = display with { Theme = 0 } },
            current with { Display = display with { CycleScreens = 0x80 } }
        })
        {
            TestAssert.Throws<ArgumentException>(() => WireViewConfigCodec.Patch(bytes, invalid, firmwareVersion: 4));
        }

        TestAssert.True(bytes.SequenceEqual(original), "Validation must not mutate the device configuration.");
        TestAssert.Throws<ArgumentException>(() => WireViewConfigCodec.Patch(bytes, current, firmwareVersion: 2));

        var encoded = WireViewConfigCodec.Patch(bytes, current with { WireOcpFaultThresholdA = 25.5, Averaging = "2834ms" }, firmwareVersion: 5);
        TestAssert.Equal(byte.MaxValue, encoded[57]);
        TestAssert.Equal((byte)7, encoded[64]);
    }

    private static void SavesOnlyAfterReadBackAndVerifiesReloadedSettings()
    {
        var desired = CreateConfigBytes(96);
        var frames = new List<byte[]>();
        var reads = 0;
        byte[] Read()
        {
            reads++;
            if (reads == 1)
            {
                TestAssert.Equal(2, frames.Count);
                TestAssert.True(frames.All(frame => frame[0] == 0x06), "Settings must be verified before storing.");
            }
            else
            {
                TestAssert.Equal((byte)2, frames[2][5]);
                TestAssert.Equal((byte)1, frames[3][5]);
            }

            var readBack = (byte[])desired.Clone();
            readBack[0] = 0x7F;
            return readBack;
        }

        var saved = WireViewProReader.WriteAndVerifyConfig(desired, frames.Add, Read, _ => { });
        TestAssert.Equal(2, reads);
        TestAssert.Equal(64, frames[0].Length);
        TestAssert.Equal((byte)62, frames[1][1]);
        TestAssert.True(desired.SequenceEqual(frames.Take(2).SelectMany(frame => frame.Skip(2))), "Both write frames must carry the full payload.");
        TestAssert.Equal((byte)0x7F, saved[0]);

        frames.Clear();
        var wrong = (byte[])desired.Clone();
        wrong[44] ^= 1;

        try
        {
            WireViewProReader.WriteAndVerifyConfig(desired, frames.Add, () => wrong, _ => { });
            throw new InvalidOperationException("A mismatched read-back was accepted.");
        }
        catch (IOException)
        {
            TestAssert.Equal(2, frames.Count);
        }
    }

    private static byte[] CreateConfigBytes(int size)
    {
        var bytes = new byte[size];
        Encoding.ASCII.GetBytes("Test device").CopyTo(bytes, 3);
        bytes[36] = 1;
        bytes[37] = 4;
        bytes[38] = 20;
        bytes[39] = 100;
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(40), 350);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(42), 700);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(46), 0x3E);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(52), 0x08);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(54), 800);
        bytes[56] = 55;
        bytes[57] = 92;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(58), 600);
        bytes[60] = 30;
        bytes[61] = 6;
        bytes[62] = 3;
        bytes[63] = 1;
        return bytes;
    }
}
