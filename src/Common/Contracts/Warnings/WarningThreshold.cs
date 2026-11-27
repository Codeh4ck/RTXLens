using RTXLens.Contracts.Enums;

namespace RTXLens.Contracts.Warnings;

public sealed record WarningThreshold(string Title, SensorUnit Unit, double Limit, double? Measured, bool? OutsideLimit)
{
    public bool IsMinimum { get; init; }
    public int? Pin { get; init; }
    public WarningThresholdPin[] OffendingPins { get; init; } = [];
    public int? DurationMs { get; init; }
    public double? MinimumCurrent { get; init; }
    public bool? Detected { get; init; }
}

public sealed record WarningThresholdPin(int Pin, double Value);
