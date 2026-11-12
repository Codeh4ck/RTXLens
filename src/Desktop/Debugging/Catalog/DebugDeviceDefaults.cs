using RTXLens.Plugins.WireView.Contracts.Common;

namespace RTXLens.Debugging.Catalog;

public static class DebugDeviceDefaults
{
    public static DeviceConfig WireView() => new()
    {
        Version = 2,
        FirmwareVersion = 5,
        BacklightDuty = 70,
        Averaging = "354ms",
        Display = new DeviceDisplayConfig
        {
            DefaultScreen = 0,
            CurrentScale = 1,
            CycleScreens = 0x1F,
            CycleTimeS = 5,
            TimeoutS = 30,
            PrimaryColor = 0xFFFFFFFF,
            SecondaryColor = 0xFFA8BCCF,
            HighlightColor = 0xFF22D3BE,
            BackgroundColor = 0xFF0C121A,
            BackgroundBitmapId = 255,
            FanBitmapId = 0x64,
            Inversion = 0
        },
        FriendlyName = "DEBUG WireView Pro II",
        WireOcpFaultThresholdA = 9.2,
        OcpFaultThresholdA = 55,
        OppFaultThresholdW = 600,
        TsFaultThresholdC = 80,
        FanMode = "Curve",
        FanTempSource = "TsIn",
        FanDutyMax = 100,
        FanTempMinC = 35,
        FanTempMaxC = 70
    };
}