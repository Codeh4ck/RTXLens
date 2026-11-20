namespace RTXLens.Plugins.Asus.Contracts.Warnings;

public static class AsusWarningIds
{
    public const string Overcurrent = AsusIdentity.Id + "/pin-overcurrent";
    public const string Imbalance = AsusIdentity.Id + "/current-imbalance";
    public const string LowVoltage = AsusIdentity.Id + "/low-pin-voltage";
    public const string HighVoltage = AsusIdentity.Id + "/high-pin-voltage";
    public const string CoreTemperature = AsusIdentity.Id + "/core-temperature";
    public const string MemoryTemperature = AsusIdentity.Id + "/memory-temperature";
}
