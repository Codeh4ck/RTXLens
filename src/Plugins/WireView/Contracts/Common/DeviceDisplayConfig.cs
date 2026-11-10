namespace RTXLens.Plugins.WireView.Contracts.Common;

public sealed record DeviceDisplayConfig
{
    public byte CurrentScale { get; init; }
    public byte PowerScale { get; init; }
    public byte Rotation { get; init; }
    public byte TimeoutMode { get; init; }
    public byte CycleScreens { get; init; }
    public byte CycleTimeS { get; init; }
    public byte TimeoutS { get; init; }
    public byte? Theme { get; init; }
    public byte? DefaultScreen { get; init; }
    public uint? PrimaryColor { get; init; }
    public uint? SecondaryColor { get; init; }
    public uint? HighlightColor { get; init; }
    public uint? BackgroundColor { get; init; }
    public byte? BackgroundBitmapId { get; init; }
    public byte? FanBitmapId { get; init; }
    public byte? Inversion { get; init; }
}
