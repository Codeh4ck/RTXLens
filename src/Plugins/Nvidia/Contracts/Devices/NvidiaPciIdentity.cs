using System.Globalization;

namespace RTXLens.Plugins.GenericNvidia.Contracts.Devices;

public static class NvidiaPciIdentity
{
    public static bool TryParse(string id, out (uint Bus, uint Slot, uint DeviceId, uint SubsystemId) identity)
    {
        identity = default;

        var parts = id.Split('/');

        return parts is ["nvidia-pci", _, _, _, _]
               && Hex(parts[1], out identity.Bus) && Hex(parts[2], out identity.Slot)
               && Hex(parts[3], out identity.DeviceId) && Hex(parts[4], out identity.SubsystemId);

        static bool Hex(string text, out uint value) => uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }

    // The low word is the ASUS subsystem vendor. Unknown boards must never receive I2C probes
    public static bool IsAstral(uint subsystemId) => subsystemId is
        0x89DF1043 or 0x89DE1043 or 0x8A2B1043 or 0x8A451043 or // 5080, OC, White OC, Miku
        0x89E31043 or 0x8A2E1043 or 0x89EC1043 or 0x8A611043 or // 5090 OC, White, LC, Matrix
        0x89EA1043 or 0x89FF1043 or // 5090D OC and ASUS-built OEM
        0x8A5A1043 or 0x8A3C1043; // BTF revisions
}
