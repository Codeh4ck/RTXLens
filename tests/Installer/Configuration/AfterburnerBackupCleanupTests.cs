using System;
using System.Diagnostics;
using System.IO;
using RTXLens.Installer.Actions.Configuration;
using RTXLens.Installer.Actions.Validation;

namespace RTXLens.Installer.Tests.Configuration;

internal static class AfterburnerBackupCleanupTests
{
    private const string Archive = "20261109-044139-100-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Key = "0123456789ABCDEF0123";

    public static void Run()
    {
        var fixture = Path.Combine(Path.GetTempPath(), "rtx-afterburner-cleanup-" + Guid.NewGuid().ToString("N"));
        var installation = Path.Combine(fixture, "Installation");
        var desktop = Path.Combine(installation, "Desktop");
        var store = Path.Combine(desktop, "AfterburnerBackups");
        var profiles = Path.Combine(desktop, "config");
        var activeProfile = Path.Combine(profiles, "active-profile.txt");
        var chartProfile = Path.Combine(profiles, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.profile.json");
        var warningProfiles = Path.Combine(profiles, "device-warnings.json");
        var programData = Path.Combine(fixture, "ProgramData");
        var legacy = Path.Combine(programData, "RTXLens.AfterburnerBackups", Key);
        var external = Path.Combine(fixture, "MSI Afterburner");
        var link = Path.Combine(store, ".import-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        try
        {
            WriteArchive(store);
            WriteArchive(legacy);
            File.WriteAllText(Path.Combine(store, ".operation.lock"), "");
            File.WriteAllText(Path.Combine(store, "legacy-import-" + Key + ".txt"), "");
            File.WriteAllText(Path.Combine(legacy, "pending.json"), Archive);
            var results = Directory.CreateDirectory(Path.Combine(store, "OperationResults"));
            File.WriteAllText(Path.Combine(results.FullName, "cccccccccccccccccccccccccccccccc.json"), "");
            Check.That(InstallationPreflight.ContainsOnlyPreservedData(installation),
                "A reinstall accepts the backup tree left by an older uninstaller.");

            Directory.CreateDirectory(profiles);
            File.WriteAllText(activeProfile, "auto");
            File.WriteAllText(chartProfile, "{}");
            File.WriteAllText(warningProfiles, "[]");
            Check.That(InstallationPreflight.ContainsOnlyPreservedData(installation),
                "Reinstall accepts saved profiles alongside leftover backups.");

            var otherFile = Path.Combine(profiles, "unrelated.profile.json");
            File.WriteAllText(otherFile, "keep");
            Check.That(!InstallationPreflight.ContainsOnlyPreservedData(installation),
                "Reinstall rejects unrelated files even inside the profile directory.");
            File.Delete(otherFile);

            otherFile = Path.Combine(desktop, "unrelated.txt");
            File.WriteAllText(otherFile, "keep");
            Check.That(!InstallationPreflight.ContainsOnlyPreservedData(installation),
                "Reinstall still rejects unrelated files.");
            File.Delete(otherFile);

            Directory.CreateDirectory(external);
            var liveConfig = Path.Combine(external, "MSIAfterburner.cfg");
            File.WriteAllText(liveConfig, "live configuration");
            CreateJunction(link, external);
            Check.That(!InstallationPreflight.ContainsOnlyPreservedData(installation),
                "Reinstall rejects a linked backup directory.");

            var smtp = Path.Combine(programData, "RTXLens.Safeguard", "mail", "record.json");
            Directory.CreateDirectory(Path.GetDirectoryName(smtp)!);
            File.WriteAllText(smtp, "credential placeholder");

            AfterburnerBackupCleanup.Remove(installation, programData, message => throw new InvalidOperationException(message));
            Check.That(!Directory.Exists(Path.Combine(store, Archive)) && !File.Exists(Path.Combine(store, ".operation.lock"))
                && !File.Exists(Path.Combine(store, "legacy-import-" + Key + ".txt")) && !Directory.Exists(Path.GetDirectoryName(legacy))
                && !Directory.Exists(results.FullName),
                "Uninstall removes recognized files from both backup locations.");
            Check.That(File.Exists(liveConfig) && File.Exists(smtp) && Directory.Exists(link),
                "Uninstall preserves the live Afterburner configuration, SMTP data and linked targets.");

            Directory.Delete(link, recursive: false);
            AfterburnerBackupCleanup.Remove(installation, programData, message => throw new InvalidOperationException(message));
            Check.That(!Directory.Exists(store), "Uninstall removes the backup directory once it is empty.");
            Check.That(File.Exists(activeProfile) && File.Exists(chartProfile) && File.Exists(warningProfiles)
                && InstallationPreflight.ContainsOnlyPreservedData(installation),
                "Reinstall accepts the saved profiles that remain after uninstall.");

            var linkedProfiles = Path.Combine(fixture, "saved-profiles");
            Directory.Move(profiles, linkedProfiles);
            try
            {
                CreateJunction(profiles, linkedProfiles);
                Check.That(!InstallationPreflight.ContainsOnlyPreservedData(installation), "Reinstall rejects a linked profile directory.");
            }
            finally
            {
                if (Directory.Exists(profiles))
                {
                    Directory.Delete(profiles, recursive: false);
                }
            }
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link, recursive: false);
            }

            var target = Path.GetFullPath(fixture);
            var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(target).StartsWith("rtx-afterburner-cleanup-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Invalid backup cleanup test path.");
            }

            Directory.Delete(target, recursive: true);
        }
    }

    private static void WriteArchive(string store)
    {
        var directory = Path.Combine(store, Archive);
        Directory.CreateDirectory(directory);
        foreach (var name in new[] { "PSU.cfg", "MSIAfterburner.cfg", "manifest.json" })
        {
            File.WriteAllText(Path.Combine(directory, name), "");
        }
    }

    private static void CreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Could not start junction fixture creation.");
        process.WaitForExit();
        Check.That(process.ExitCode == 0, "The junction fixture was created.");
    }
}
