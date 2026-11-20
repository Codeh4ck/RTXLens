using RTXLens.Contracts.Warnings;

namespace RTXLens.Contracts.Configuration;

public sealed record SettingsProfile
{
    public const string AutomaticId = "auto";
    public const string Rtx5080Id = "rtx-5080";
    public const string Rtx5090Id = "rtx-5090";

    public string Id { get; init; } = Rtx5090Id;
    public string Name { get; init; } = "RTX 5090";
    public string GpuModel { get; init; } = "5090";
    public double CurrentCeiling { get; init; } = 10;
    public double? PowerCeilingPerPin { get; init; }
    public double PowerFallbackPerPin { get; init; } = 100;
    public double VoltageMinimum { get; init; } = 11.4;
    public double VoltageMaximum { get; init; } = 12.6;
    public bool UseWireViewOvercurrentFallback { get; init; }
    public double OvercurrentAmperes { get; init; } = 9.2;
    public int OvercurrentMilliseconds { get; init; } = 3000;
    public int ImbalanceActivationAmperes { get; init; } = 6;
    public int ImbalancePercentage { get; init; } = 30;
    public int ImbalanceMilliseconds { get; init; } = 3000;
    public double LowPinVoltage { get; init; } = 11.5;
    public double HighPinVoltage { get; init; } = 12.5;
    public double? CoreTemperature { get; init; } = 81;
    public double? MemoryTemperature { get; init; } = 85;
    public WarningOptions Notifications { get; init; } = new();

    public static SettingsProfile Rtx5090 => new();

    public static SettingsProfile Rtx5080 => new()
    {
        Id = Rtx5080Id,
        Name = "RTX 5080",
        GpuModel = "5080",
        CurrentCeiling = 6,
        PowerCeilingPerPin = 75,
        UseWireViewOvercurrentFallback = true,
        OvercurrentAmperes = 6,
        ImbalanceActivationAmperes = 5,
        ImbalancePercentage = 20
    };

    public bool IsValid() => !string.IsNullOrWhiteSpace(Id) && !string.IsNullOrWhiteSpace(Name) && Name.Length <= 80
                             && GpuModel is "5080" or "5090"
                             && CurrentCeiling is >= 1 and <= 30
                             && (PowerCeilingPerPin is null or >= 1 and <= 250)
                             && PowerFallbackPerPin is >= 1 and <= 250
                             && VoltageMinimum is >= 5 and < 12 && VoltageMaximum is > 12 and <= 20
                             && OvercurrentAmperes is >= 6 and <= 10.5
                             && OvercurrentMilliseconds is >= 100 and <= 30000
                             && ImbalanceActivationAmperes is >= 1 and <= 10
                             && ImbalancePercentage is >= 10 and <= 50
                             && ImbalanceMilliseconds is >= 100 and <= 30000
                             && LowPinVoltage is >= 5 and < 12 && HighPinVoltage is > 12 and <= 20
                             && (CoreTemperature is null or >= 30 and <= 120)
                             && (MemoryTemperature is null or >= 30 and <= 120) && Notifications.IsValid();

    public double ResolvePowerCeiling(double? cableRating) => PowerCeilingPerPin
        ?? (cableRating is > 0 ? cableRating.Value / 6 : PowerFallbackPerPin);
}
