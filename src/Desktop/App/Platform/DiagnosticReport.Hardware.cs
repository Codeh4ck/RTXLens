using System.Management;
using System.Runtime.Versioning;
using System.Text.Json;
using LibreHardwareMonitor.Hardware;
using RTXLens.Plugins.Msi.Device;

namespace RTXLens.App.Platform;

internal sealed partial class DiagnosticReport
{
    private sealed record HardwareProduct(string? Model, string? Vendor);

    [SupportedOSPlatform("windows")]
    internal void CollectHardwareInventory()
    {
        List<HardwareProduct> processors = [], memory = [], graphics = [], supplies = [];
        TryCollect("SMBIOS processors and RAM", () =>
        {
            // The full SMBIOS report also contains serial numbers, asset tags and UUIDs.
            var bios = new SMBios();
            foreach (var cpu in bios.Processors ?? [])
                if (ProductText(cpu.Version) is { } model)
                    processors.Add(new(model, ProductText(cpu.ManufacturerName)));
            foreach (var dimm in bios.MemoryDevices ?? [])
                if (dimm.Size != 0)
                    memory.Add(new(ProductText(dimm.PartNumber), ProductText(dimm.ManufacturerName)));
        });

        if (processors.Count == 0)
            TryCollect("Windows processors", () => Query("SELECT Name, Manufacturer FROM Win32_Processor",
                row => processors.Add(new(ProductText(row["Name"]), ProductText(row["Manufacturer"])))));
        if (memory.Count == 0)
            TryCollect("Windows RAM", () => Query("SELECT PartNumber, Manufacturer FROM Win32_PhysicalMemory",
                row => memory.Add(new(ProductText(row["PartNumber"]), ProductText(row["Manufacturer"])))));
        TryCollect("Windows graphics adapters", () =>
            Query("SELECT Name, AdapterCompatibility FROM Win32_VideoController",
                row => graphics.Add(new(ProductText(row["Name"]), ProductText(row["AdapterCompatibility"])))));
        TryCollect("USB power supplies", () =>
            Query(@"SELECT HardwareID FROM Win32_PnPEntity WHERE Present = TRUE AND (DeviceID LIKE 'USB\\VID_0DB0%' OR DeviceID LIKE 'USB\\VID_1B1C%')",
                row =>
                {
                    // Match the physical USB node once. Composite interface IDs contain an additional MI field.
                    if (row["HardwareID"] is not string[] ids) return;
                    foreach (var id in ids)
                        if (KnownPowerSupply(id) is { } supply)
                        {
                            supplies.Add(supply);
                            break;
                        }
                }));
        File.WriteAllText(PathFor("current/hardware-inventory.json"),
            JsonSerializer.Serialize(new { processors, memoryModules = memory, graphicsAdapters = graphics, powerSupplies = supplies }, Json));
        _notes.Add($"Detected hardware: {processors.Count} CPU(s), {memory.Count} RAM module(s), {graphics.Count} graphics adapter(s), {supplies.Count} identifiable USB PSU(s).");

        void Query(string query, Action<ManagementBaseObject> visit)
        {
            using var search = new ManagementObjectSearcher(new ManagementScope(@"root\cimv2"), new ObjectQuery(query),
                new System.Management.EnumerationOptions { ReturnImmediately = true, Rewindable = false, Timeout = TimeSpan.FromSeconds(5) });
            using var rows = search.Get();
            foreach (ManagementBaseObject row in rows)
                using (row)
                {
                    token.ThrowIfCancellationRequested();
                    visit(row);
                }
        }
    }

    // Only accept the model and vendor fields, never a WMI object, firmware report or instance path.
    private static string? ProductText(object? raw)
    {
        if (raw is not string value) return null;
        var text = value.Trim();
        if (text.Length is 0 or > 160 || text.Any(char.IsControl) || text.Contains('\\') || text.Contains('@') ||
            text.Contains("://", StringComparison.Ordinal) || Guid.TryParse(text, out _)) return null;
        return text.ToUpperInvariant() is "UNKNOWN" or "NONE" or "NOT SPECIFIED" or "TO BE FILLED BY O.E.M." ? null : text;
    }

    private static HardwareProduct? KnownPowerSupply(string hardwareId)
    {
        // A USB hardware ID shared by every unit of this product, without revision or instance suffix.
        if (hardwareId.Length != 21 || !hardwareId.StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase)
            || !hardwareId.AsSpan(12, 5).Equals("&PID_", StringComparison.OrdinalIgnoreCase)
            || !ushort.TryParse(hardwareId.AsSpan(8, 4), System.Globalization.NumberStyles.HexNumber, null, out var vendor)
            || !ushort.TryParse(hardwareId.AsSpan(17), System.Globalization.NumberStyles.HexNumber, null, out var product)) return null;
        if (vendor == MsiPsuModels.VendorId)
        {
            var model = product == 0x56D4 ? "MEG Ai1300P" : MsiPsuModels.Name(vendor, product);
            return model == null ? null : new(model, "MSI");
        }
        if (vendor != 0x1B1C) return null;
        var corsair = product switch
        {
            0x1C03 => "HX550i", 0x1C04 => "HX650i", 0x1C05 => "HX750i", 0x1C06 => "HX850i",
            0x1C07 => "HX1000i", 0x1C1E => "HX1000i Rev. 2", 0x1C08 => "HX1200i", 0x1C1F => "HX1500i",
            0x1C09 => "RM550i", 0x1C0A => "RM650i", 0x1C0B => "RM750i", 0x1C0C => "RM850i",
            0x1C0D => "RM1000i", 0x1C11 => "AX1600i", _ => null
        };
        return corsair == null ? null : new(corsair, "Corsair");
    }
}
