namespace RTXLens.Plugins.Msi.Device;

internal static class MsiPsuModels
{
    public const int VendorId = 0x0DB0;
    public const int Ai1300TsProductId = 0xAA6F;
    public const int Ai1600TsProductId = 0x808C;

    public const string Ai1300TsName = "MPG Ai1300TS";
    public const string Ai1600TsName = "MPG Ai1600TS";

    public static string? Name(int vendorId, int productId) => vendorId != VendorId ? null : productId switch
    {
        Ai1300TsProductId => Ai1300TsName,
        Ai1600TsProductId => Ai1600TsName,
        _ => null
    };

    public static bool Matches(int productId, string model) => Name(VendorId, productId) is { } name && name == model;
}
