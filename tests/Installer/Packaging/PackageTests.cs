using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using WixToolset.Dtf.WindowsInstaller;

namespace RTXLens.Installer.Tests.Packaging;

internal static class PackageTests
{
    public static void Run(string path, string payload)
    {
        using var database = new Database(path, DatabaseOpenMode.ReadOnly);
        PlatformRequirementsTests.Run(path, database);
        var features = Read(database, "SELECT `Feature`, `Attributes` FROM `Feature`");
        Check.That(features.Count == 2 && features.Any(row => row[0] == "Service") && features.Any(row => row[0] == "Desktop"), "MSI contains exactly the service and desktop component choices.");
        Check.That((int.Parse(features.Single(row => row[0] == "Service")[1]) & (int)FeatureAttributes.UIDisallowAbsent) != 0, "The service is required in the component picker.");
        Check.That((int.Parse(features.Single(row => row[0] == "Desktop")[1]) & (int)FeatureAttributes.UIDisallowAbsent) == 0, "The desktop app remains optional and removable.");

        var directories = Read(database, "SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`").ToDictionary(row => row[0]);
        var components = Read(database, "SELECT `Component`, `Directory_`, `Attributes` FROM `Component`").ToDictionary(row => row[0]);
        var files = Read(database, "SELECT `File`, `Component_`, `FileName`, `FileSize`, `Language` FROM `File`");
        var payloadRoot = Path.GetFullPath(payload).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var expected = Directory.GetFiles(payloadRoot, "*", SearchOption.AllDirectories).ToDictionary(file => file.Substring(payloadRoot.Length), StringComparer.OrdinalIgnoreCase);
        foreach (var row in files)
        {
            var directory = components[row[1]][1];
            var parts = new List<string> { LongName(row[2]) };
            while (directory != "INSTALLFOLDER")
            {
                var entry = directories[directory];
                var name = LongName(entry[2].Split(':')[0]);
                if (name != ".")
                {
                    parts.Insert(0, name);
                }

                directory = entry[1];
            }

            var relative = Path.Combine([.. parts]);
            Check.That(expected.TryGetValue(relative, out var source), "Payload is mapped to the correct installation folder: " + relative, false);
            Check.That(new FileInfo(source!).Length == long.Parse(row[3]), "MSI file size matches the published payload: " + relative, false);
            expected.Remove(relative);
            CheckLanguageMetadata(row[4], relative);
            Check.That((int.Parse(components[row[1]][2]) & 256) != 0, "Payload component uses the x64 installation view: " + relative, false);
        }

        Check.That(expected.Count == 0, "Every published DLL, executable, PRI, XBF, asset and runtime file is included exactly once.");

        var shortcut = Read(database, "SELECT `Component_`, `Target` FROM `Shortcut` WHERE `Shortcut`='StartMenuShortcut'").Single();
        Check.That(components[shortcut[0]][1] == "MonitorMenuFolder" && shortcut[1] == "[AppInstallFolder]RTXLens.App.exe",
            "The Start menu shortcut targets the desktop executable directly across MSI versions.");
        var shortcutIdentity = Read(database, "SELECT `PropVariantValue` FROM `MsiShortcutProperty` WHERE `Shortcut_`='StartMenuShortcut' AND `PropertyKey`='System.AppUserModel.ID'").Single();
        Check.That(shortcutIdentity[0] == "Codeh4ck.RTXLens", "The Start menu shortcut uses the stable desktop app identity.");

        var service = Read(database, "SELECT `Name`, `StartType`, `StartName`, `Arguments` FROM `ServiceInstall`").Single();
        Check.That(service[0] == "RTXLensService" && service[1] == "2" && service[2] == "LocalSystem", "Service registers automatically under LocalSystem.");
        Check.That(service[3].Contains("--contentRoot"), "Service content root points to its installed payload.");
        var control = Read(database, "SELECT `Name`, `Event` FROM `ServiceControl`").Single();
        Check.That((int.Parse(control[1]) & 0x80) != 0 && (int.Parse(control[1]) & 0x20) != 0, "Uninstall stops and deletes the registered service.");
        var sequence = Read(database, "SELECT `Action`, `Sequence`, `Condition` FROM `InstallExecuteSequence`").ToDictionary(row => row[0]);
        Check.That(int.Parse(sequence["Preflight"][1]) < int.Parse(sequence["InstallValidate"][1]), "Ownership checks and Afterburner shutdown run before Windows Installer validates the installation.");
        Check.That(int.Parse(sequence["StopServices"][1]) < int.Parse(sequence["PortAvailable"][1]), "Port check runs after stopping this product's own service.");
        Check.That(int.Parse(sequence["RestoreConfiguration"][1]) < int.Parse(sequence["BackupConfiguration"][1])
            && int.Parse(sequence["BackupConfiguration"][1]) < int.Parse(sequence["InstallFiles"][1]), "Configuration rollback is scheduled before payload changes.");
        Check.That(int.Parse(sequence["VerifyServiceHealth"][1]) > int.Parse(sequence["StartServices"][1]), "Actual service liveness gates installation success.");
        Check.That(sequence["RemoveStartupRegistration"][2].Contains("UPGRADINGPRODUCTCODE"), "Upgrades preserve startup registration; feature removal and uninstall clean it.");
        var actions = Read(database, "SELECT `Action`, `Type` FROM `CustomAction`").ToDictionary(row => row[0]);
        Check.That((int.Parse(actions["RestoreConfiguration"][1]) & 0x500) == 0x500, "Configuration restore is a rollback action.");
        Check.That((int.Parse(actions["RemoveStartupRegistration"][1]) & 0x600) == 0x600, "Startup cleanup occurs only when uninstall/feature removal commits.");
        int Order(string action) => int.Parse(sequence[action][1]);
        Check.That(Order("RemoveExistingProducts") < Order("InstallFiles")
            && Order("InstallFiles") < Order("PrepareLegacyMigration")
            && Order("PrepareLegacyMigration") < Order("RestoreLegacyMigration")
            && Order("RestoreLegacyMigration") < Order("CaptureLegacyMigration")
            && Order("CaptureLegacyMigration") < Order("ApplyLegacyMigration")
            && Order("ApplyLegacyMigration") < Order("StartServices"),
            "Setup migrates preserved data before starting the new service and rolls it back before restoring the old product.");
        Check.That(Order("VerifyServiceHealth") < Order("CleanLegacyMigration")
            && (int.Parse(actions["CleanLegacyMigration"][1]) & 0xE00) == 0xE00,
            "Legacy directory cleanup commits only after the replacement service is healthy.");

        var backupCleanup = sequence["RemoveAfterburnerBackups"];
        Check.That(backupCleanup[2] == "Installed AND REMOVE~=\"ALL\" AND NOT UPGRADINGPRODUCTCODE"
            && sequence["PrepareAfterburnerBackupCleanup"][2] == backupCleanup[2]
            && (int.Parse(actions["RemoveAfterburnerBackups"][1]) & 0xE00) == 0xE00,
            "Afterburner backup cleanup commits only on a full uninstall outside upgrades.");
        Check.That(int.Parse(sequence["PrepareAfterburnerBackupCleanup"][1]) < int.Parse(backupCleanup[1])
            && int.Parse(backupCleanup[1]) < int.Parse(sequence["InstallFinalize"][1]),
            "Setup captures the registered installation path before the backup cleanup commits.");
        Check.That(Read(database, "SELECT `FileKey`, `FileName` FROM `RemoveFile`").All(row => string.IsNullOrEmpty(row[1])), "Uninstall contains no configuration or wildcard file wipe action.");
        Check.That(Read(database, "SELECT `UpgradeCode` FROM `Upgrade`").Count > 0, "Stable upgrade identity and downgrade checks are authored.");
        using (var binary = database.OpenView("SELECT `Data` FROM `Binary` WHERE `Name`='InstallerActions'"))
        {
            binary.Execute();
            using var record = binary.Fetch();
            using var stream = record.GetStream(1);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var bytes = memory.ToArray();
            Check.That(BitConverter.ToUInt16(bytes, BitConverter.ToInt32(bytes, 0x3c) + 4) == 0x8664, "Embedded native custom-action host matches the x64 MSI architecture.");
        }
        Console.WriteLine($"PASS: Validated {files.Count} packaged files, OS detection and installer lifecycle tables without executing an installation.");
    }

    private static void CheckLanguageMetadata(string language, string relative)
    {
        var knownSdkFile = relative.EndsWith(".mui", StringComparison.OrdinalIgnoreCase)
            || relative.EndsWith("Microsoft.ui.xaml.dll", StringComparison.OrdinalIgnoreCase)
            || relative.EndsWith("Microsoft.UI.Xaml.Phone.dll", StringComparison.OrdinalIgnoreCase);
        if (language.Length > 20 && !knownSdkFile)
        {
            throw new InvalidDataException("Unexpected MSI language-list overflow: " + relative);
        }

        foreach (var value in language.Split([','], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(value, out var lcid) || lcid < 0 || lcid > 65535)
            {
                throw new InvalidDataException("Invalid MSI language value: " + relative);
            }

            if (lcid == 0)
            {
                continue;
            }

            try { _ = CultureInfo.GetCultureInfo(lcid); }
            catch (CultureNotFoundException) when (knownSdkFile) { }
        }
    }

    private static string LongName(string value) => value.Contains("|") ? value.Split('|')[1] : value;

    private static List<string[]> Read(Database database, string query)
    {
        var result = new List<string[]>();
        using var view = database.OpenView(query);
        view.Execute();
        Record? record;
        while ((record = view.Fetch()) is not null)
        {
            using (record)
            {
                result.Add([.. Enumerable.Range(1, record.FieldCount).Select(record.GetString)]);
            }
        }

        return result;
    }
}
