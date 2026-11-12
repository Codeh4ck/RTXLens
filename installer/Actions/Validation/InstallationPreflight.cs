using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using RTXLens.Installer.Actions.Configuration;
using RTXLens.Installer.Actions.Services;

namespace RTXLens.Installer.Actions.Validation;

public static class InstallationPreflight
{
    private const FileAttributes UnsafeEntry = FileAttributes.Directory | FileAttributes.ReparsePoint;
    private static readonly Regex ProfileFileName = new(
        @"\A(?:active-profile\.txt|device-warnings\.json|[0-9a-f]{32}\.profile\.json)(?:\.[0-9a-f]{32}\.tmp)?\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static void CheckDesktopClosed(string installDirectory, bool legacy = false)
    {
        foreach (var process in Process.GetProcessesByName(legacy ? "Rtx5090Monitoring.App" : "RTXLens.App"))
        {
            using (process)
            {
                var executable = process.MainModule?.FileName;
                if (executable is not null && IsInstalledApp(executable, installDirectory, legacy))
                {
                    throw new IOException("Exit RTX Lens through its tray menu, then retry. Unsaved configuration drafts will not be discarded automatically.");
                }
            }
        }
    }

    public static bool IsInstalledApp(string executable, string installDirectory, bool legacy = false) =>
        string.Equals(Path.GetFullPath(executable), Path.Combine(Path.GetFullPath(installDirectory), "Desktop", legacy ? "Rtx5090Monitoring.App.exe" : "RTXLens.App.exe"), StringComparison.OrdinalIgnoreCase);

    public static void CheckServiceOwnership(string installDirectory, bool alreadyInstalled, string? legacyDirectory = null)
    {
        using var registration = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\RTXLens");
        var managedDirectory = registration?.GetValue("InstallDirectory") as string;
        var managed = alreadyInstalled || (managedDirectory is not null
                                           && string.Equals(Path.GetFullPath(managedDirectory).TrimEnd(Path.DirectorySeparatorChar),
                                               Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar),
                                               StringComparison.OrdinalIgnoreCase));

        var migratingInPlace = !string.IsNullOrEmpty(legacyDirectory)
            && LegacyInstallationMigration.SamePath(installDirectory, legacyDirectory!);
        if (!managed && !migratingInPlace && Directory.Exists(installDirectory) && !ContainsOnlyPreservedData(installDirectory))
        {
            throw new IOException("The destination contains files not managed by this installer. Choose an empty dedicated folder or remove the previous unmanaged installation first.");
        }

        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\RTXLensService");
        if (key?.GetValue("ImagePath") is not string image)
        {
            return;
        }

        var expected = Path.Combine(Path.GetFullPath(installDirectory), "Service", "RTXLens.Service.exe");
        if (!managed || !image.StartsWith('"' + expected + '"', StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("An unmanaged RTXLensService installation already exists. Remove that service registration before installing this release. Runtime configuration will be preserved.");
        }
    }

    public static bool ContainsOnlyPreservedData(string installDirectory) =>
        IsPlainDirectory(installDirectory) && Directory.EnumerateFileSystemEntries(installDirectory).All(path =>
            string.Equals(Path.GetFileName(path), "Desktop", StringComparison.OrdinalIgnoreCase)
            && IsPlainDirectory(path) && Directory.EnumerateFileSystemEntries(path).All(IsPreservedDesktopDirectory));

    private static bool IsPreservedDesktopDirectory(string path)
    {
        var name = Path.GetFileName(path);
        if (string.Equals(name, "AfterburnerBackups", StringComparison.OrdinalIgnoreCase))
        {
            return AfterburnerBackupCleanup.IsKnownBackupDirectory(path);
        }

        return string.Equals(name, "config", StringComparison.OrdinalIgnoreCase) && IsPlainDirectory(path)
            && Directory.EnumerateFileSystemEntries(path).All(file =>
                (File.GetAttributes(file) & UnsafeEntry) == 0 && ProfileFileName.IsMatch(Path.GetFileName(file)));
    }

    private static bool IsPlainDirectory(string path) =>
        Directory.Exists(path) && (File.GetAttributes(path) & UnsafeEntry) == FileAttributes.Directory;

    public static void CheckLegacyServiceOwnership(string directory)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Rtx5090MonitoringService");
        if (key?.GetValue("ImagePath") is string image)
        {
            var expected = Path.Combine(Path.GetFullPath(directory), "Service", "Rtx5090Monitoring.Service.exe");
            if (!image.StartsWith('"' + expected + '"', StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Setup found an unmanaged RTX Monitor service at a different path. Resolve the service registration before retrying.");
            }
        }
    }

    public static void CheckPortAvailable()
    {
        var ownProcess = ServiceStartupConfiguration.RunningProcessId();
        foreach (var process in TcpListenerOwners.Read(59273))
        {
            if (ownProcess == 0 || process != ownProcess)
            {
                throw new IOException($"Port 59273 is already in use by process {process}. Stop the original WireView/ASUS service or other listener, then retry. RTX Lens will not stop an unrelated service.");
            }
        }
    }
}
