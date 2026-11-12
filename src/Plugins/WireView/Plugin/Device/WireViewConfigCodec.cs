using System.Buffers.Binary;
using System.Text;
using RTXLens.Plugins.WireView.Contracts.Common;

namespace RTXLens.Plugins.WireView.Device;

internal static class WireViewConfigCodec
{
    private const int VersionOffset = 2;
    private const int NameOffset = 3;
    private const int NameLength = 32;
    private const int AveragingOffset = 64;

    // The older firmware layouts have an 8-byte display block. Version 2 aligns its 28-byte display block to four bytes.
    internal static int Size(int version) => version switch { 0 => 72, 1 => 74, 2 => 96, _ => 0 };

    internal static DeviceDisplayConfig? ReadDisplay(byte[] payload, int version)
    {
        if (Size(version) == 0 || payload.Length != Size(version))
        {
            return null;
        }

        if (version < 2)
        {
            var offset = version == 0 ? 64 : 65;
            return new DeviceDisplayConfig
            {
                CurrentScale = payload[offset],
                PowerScale = payload[offset + 1],
                Theme = payload[offset + 2],
                Rotation = payload[offset + 3],
                TimeoutMode = payload[offset + 4],
                CycleScreens = payload[offset + 5],
                CycleTimeS = payload[offset + 6],
                TimeoutS = payload[offset + 7]
            };
        }

        return new DeviceDisplayConfig
        {
            DefaultScreen = payload[68],
            CurrentScale = payload[69],
            PowerScale = payload[70],
            Rotation = payload[71],
            TimeoutMode = payload[72],
            CycleScreens = payload[73],
            CycleTimeS = payload[74],
            TimeoutS = payload[75],
            PrimaryColor = ReadColor(76),
            SecondaryColor = ReadColor(80),
            HighlightColor = ReadColor(84),
            BackgroundColor = ReadColor(88),
            BackgroundBitmapId = payload[92],
            FanBitmapId = payload[93],
            Inversion = payload[94]
        };

        uint ReadColor(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset));
    }

    internal static byte[] Patch(byte[] original, DeviceConfig changes, int firmwareVersion)
    {
        var version = changes.Version;
        if (Size(version) == 0 || original.Length != Size(version) || original[VersionOffset] != version)
        {
            throw new ArgumentException("The WireView configuration layout does not support editing.");
        }

        var current = WireViewProReader.ParseConfig(original, version);
        WireViewSettingsValidation.Validate(current, changes, firmwareVersion);

        var payload = (byte[])original.Clone();
        if (changes.FriendlyName != current.FriendlyName)
        {
            var name = payload.AsSpan(NameOffset, NameLength);
            name.Clear();
            Encoding.ASCII.GetBytes(changes.FriendlyName, name);
        }

        if (changes.FanMode != current.FanMode)
        {
            payload[36] = changes.FanMode == "Curve" ? (byte)0 : (byte)1;
        }

        if (changes.FanTempSource != current.FanTempSource)
        {
            payload[37] = changes.FanTempSource switch
            {
                "TsIn" => 0,
                "TsOut" => 1,
                "Ts1" => 2,
                "Ts2" => 3,
                "Tmax" => 4,
                _ => throw new ArgumentException("Select a valid fan temperature source.")
            };
        }

        payload[38] = changes.FanDutyMin;
        payload[39] = changes.FanDutyMax;
        WriteTemperature(40, changes.FanTempMinC);
        WriteTemperature(42, changes.FanTempMaxC);

        payload[44] = changes.BacklightDuty;
        WriteUInt16(46, changes.FaultDisplayEnable);
        WriteUInt16(48, changes.FaultBuzzerEnable);
        WriteUInt16(50, changes.FaultSoftPowerEnable);
        WriteUInt16(52, changes.FaultHardPowerEnable);
        WriteTemperature(54, changes.TsFaultThresholdC);
        payload[56] = changes.OcpFaultThresholdA;
        payload[57] = checked((byte)Math.Round(changes.WireOcpFaultThresholdA * 10));
        WriteUInt16(58, changes.OppFaultThresholdW);
        payload[60] = changes.CurrentImbalanceThreshold;
        payload[61] = changes.CurrentImbalanceMinLoad;
        payload[62] = changes.ShutdownWaitTimeS;
        payload[63] = changes.LoggingInterval;

        if (changes.Averaging != current.Averaging)
        {
            payload[AveragingOffset] = checked((byte)Array.IndexOf([.. DeviceConfig.AveragingOptions], changes.Averaging));
        }

        var display = changes.Display!;
        if (version < 2)
        {
            var offset = version == 0 ? 64 : 65;
            payload[offset] = display.CurrentScale;
            payload[offset + 1] = display.PowerScale;
            payload[offset + 2] = display.Theme!.Value;
            WriteCommonDisplay(offset + 3);
        }
        else
        {
            payload[68] = display.DefaultScreen!.Value;
            payload[69] = display.CurrentScale;
            payload[70] = display.PowerScale;
            WriteCommonDisplay(71);
            WriteColor(76, display.PrimaryColor!.Value);
            WriteColor(80, display.SecondaryColor!.Value);
            WriteColor(84, display.HighlightColor!.Value);
            WriteColor(88, display.BackgroundColor!.Value);
            payload[92] = display.BackgroundBitmapId!.Value;
            payload[93] = display.FanBitmapId!.Value;
            payload[94] = display.Inversion!.Value;
        }

        return payload;

        void WriteTemperature(int offset, double value) =>
            BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(offset), checked((short)Math.Round(value * 10)));

        void WriteUInt16(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset), value);

        void WriteColor(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset), value);

        void WriteCommonDisplay(int offset)
        {
            payload[offset] = display.Rotation;
            payload[offset + 1] = display.TimeoutMode;
            payload[offset + 2] = display.CycleScreens;
            payload[offset + 3] = display.CycleTimeS;
            payload[offset + 4] = display.TimeoutS;
        }
    }
}
