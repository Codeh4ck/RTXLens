using RTXLens.Contracts.Monitoring;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
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

    public static void CheckDesktopClosed(string installDirectory)
    {
        foreach (var process in Process.GetProcessesByName("RTXLens.App"))
        {
            using (process)
            {
                var executable = process.MainModule?.FileName;
                if (executable is not null && IsInstalledApp(executable, installDirectory))
                {
                    throw new IOException("Exit RTX Lens through its tray menu, then retry. Unsaved configuration drafts will not be discarded automatically.");
                }
            }
        }
    }

    public static bool IsInstalledApp(string executable, string installDirectory) =>
        string.Equals(Path.GetFullPath(executable), Path.Combine(Path.GetFullPath(installDirectory), "Desktop", "RTXLens.App.exe"), StringComparison.OrdinalIgnoreCase);

    public static void CheckServiceOwnership(string installDirectory, bool alreadyInstalled, IReadOnlyDictionary<string, string> notifierHashes)
    {
        using var registration = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\RTXLens");
        var managedDirectory = registration?.GetValue("InstallDirectory") as string;
        var managed = alreadyInstalled || (managedDirectory is not null
                                           && string.Equals(Path.GetFullPath(managedDirectory).TrimEnd(Path.DirectorySeparatorChar),
                                               Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar),
                                               StringComparison.OrdinalIgnoreCase));

        if (!managed && Directory.Exists(installDirectory) && !ContainsOnlyPreservedData(installDirectory, notifierHashes))
        {
            throw new IOException("The destination contains files not managed by this installer. Choose an empty dedicated folder or remove the previous unmanaged installation first.");
        }

        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + WindowsServiceIdentity.Name);
        if (key?.GetValue("ImagePath") is not string image)
        {
            return;
        }

        var expected = Path.Combine(Path.GetFullPath(installDirectory), "Service", WindowsServiceIdentity.Executable);
        if (!managed || !image.StartsWith('"' + expected + '"', StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("An unmanaged RTXLensService installation already exists. Remove that service registration before installing this release. Runtime configuration will be preserved.");
        }
    }

    public static Dictionary<string, string> ReadNotifierHashes(Stream manifest)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StreamReader(manifest);
        while (reader.ReadLine() is { } line)
        {
            hashes.Add(line.Substring(66).Replace('/', Path.DirectorySeparatorChar), line.Substring(0, 64));
        }
        return hashes;
    }

    public static bool ContainsOnlyPreservedData(string installDirectory, IReadOnlyDictionary<string, string>? notifierHashes = null) =>
        IsPlainDirectory(installDirectory) && Directory.EnumerateFileSystemEntries(installDirectory).All(path =>
        {
            if (!IsPlainDirectory(path))
            {
                return false;
            }

            if (string.Equals(Path.GetFileName(path), "Notifier", StringComparison.OrdinalIgnoreCase))
            {
                return IsKnownNotifierDirectory(path, string.Empty, notifierHashes);
            }

            return string.Equals(Path.GetFileName(path), "Desktop", StringComparison.OrdinalIgnoreCase)
                   && Directory.EnumerateFileSystemEntries(path).All(IsPreservedDesktopDirectory);
        });

    private static bool IsKnownNotifierDirectory(string directory, string prefix, IReadOnlyDictionary<string, string>? hashes)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var name = Path.Combine(prefix, Path.GetFileName(path));
            var attributes = File.GetAttributes(path);

            if ((attributes & FileAttributes.ReparsePoint) != 0 || hashes is null)
            {
                return false;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                var childPrefix = name + Path.DirectorySeparatorChar;
                if (!hashes.Keys.Any(key => key.StartsWith(childPrefix, StringComparison.OrdinalIgnoreCase)) || !IsKnownNotifierDirectory(path, name, hashes))
                {
                    return false;
                }

                continue;
            }

            if (!hashes.TryGetValue(name, out var expected))
            {
                return false;
            }

            using var file = File.OpenRead(path);
            using var sha = SHA256.Create();

            var actual = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", string.Empty);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

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

    public static void CheckPortAvailable()
    {
        var ownProcess = ServiceStartupConfiguration.RunningProcessId();
        foreach (var process in TcpListenerOwners.Read(59273))
        {
            if (ownProcess == 0 || process != ownProcess)
            {
                throw new IOException($"Port 59273 is already in use by process {process}. Stop the application using that port, then retry. RTX Lens will not stop an unrelated service.");
            }
        }
    }
}
