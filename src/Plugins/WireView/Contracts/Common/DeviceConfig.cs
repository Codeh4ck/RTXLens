namespace RTXLens.Plugins.WireView.Contracts.Common;

public sealed record DeviceConfig
{
    public static IReadOnlyList<string> AveragingOptions { get; } = Array.AsReadOnly(new[]
    {
        "22ms", "44ms", "89ms", "177ms", "354ms", "709ms", "1417ms", "2834ms", "5668ms"
    });

    public string? EditToken { get; init; }
    public byte? FirmwareVersion { get; init; }
    public DeviceDisplayConfig? Display { get; init; }
    public int Version { get; init; }
    public string FriendlyName { get; init; } = string.Empty;
    public byte BacklightDuty { get; init; }
    public byte LoggingInterval { get; init; }
    public string? Averaging { get; init; }
    public string FanMode { get; init; } = string.Empty;
    public string FanTempSource { get; init; } = string.Empty;
    public byte FanDutyMin { get; init; }
    public byte FanDutyMax { get; init; }
    public double FanTempMinC { get; init; }
    public double FanTempMaxC { get; init; }
    public double TsFaultThresholdC { get; init; }
    public byte OcpFaultThresholdA { get; init; }
    public double WireOcpFaultThresholdA { get; init; }
    public ushort OppFaultThresholdW { get; init; }
    public byte CurrentImbalanceThreshold { get; init; }
    public byte CurrentImbalanceMinLoad { get; init; }
    public byte ShutdownWaitTimeS { get; init; }
    public ushort FaultDisplayEnable { get; init; }
    public ushort FaultBuzzerEnable { get; init; }
    public ushort FaultSoftPowerEnable { get; init; }
    public ushort FaultHardPowerEnable { get; init; }
}
