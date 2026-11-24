namespace RTXLens.Hardware.Nvidia;

public sealed record NvidiaDevice(string Id, string LegacyId, string Name)
{
    public uint SubsystemId { get; init; }
    public bool IsAstral => AstralBoard.IsSupported(SubsystemId);
}
