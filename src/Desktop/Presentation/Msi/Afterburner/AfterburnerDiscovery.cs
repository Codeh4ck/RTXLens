using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace RTXLens.Presentation.Msi.Afterburner;

[SupportedOSPlatform("windows")]
public static class AfterburnerDiscovery
{
    public static AfterburnerInstallation[] Find(string? rememberedDirectory = null)
    {
        var candidates = new List<string>();

        foreach (var process in Process.GetProcessesByName("MSIAfterburner"))
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName is { } executable)
                    {
                        candidates.Add(Path.GetDirectoryName(executable)!);
                    }
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Registry and manual discovery remain available when another user's process is inaccessible.
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(rememberedDirectory))
        {
            candidates.Add(rememberedDirectory);
        }

        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                using var registry = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null)
                {
                    continue;
                }

                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(name);
                    if (key?.GetValue("DisplayName") is not string displayName
                        || !displayName.Contains("MSI Afterburner", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (key.GetValue("InstallLocation") is string location && !string.IsNullOrWhiteSpace(location))
                    {
                        candidates.Add(location.Trim('"'));
                    }

                    if (key.GetValue("DisplayIcon") is string icon)
                    {
                        var exe = icon.Split(',')[0].Trim().Trim('"');
                        if (Path.GetDirectoryName(exe) is { Length: > 0 } directory)
                        {
                            candidates.Add(directory);
                        }
                    }
                }
            }
        }

        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "MSI Afterburner"));
        var installations = new List<AfterburnerInstallation>();

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                installations.Add(Inspect(candidate));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Invalid candidates must never become write targets.
            }
        }

        return [.. installations.DistinctBy(item => item.Directory, StringComparer.OrdinalIgnoreCase)];
    }

    public static AfterburnerInstallation Inspect(string directory)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        AfterburnerFileStore.EnsureNoLinks(directory);
        var executable = Path.Combine(directory, "MSIAfterburner.exe");
        AfterburnerFileStore.EnsureNoLinks(executable);

        if (!File.Exists(executable))
        {
            throw new InvalidDataException("MSIAfterburner.exe was not found in the selected directory.");
        }

        var version = FileVersionInfo.GetVersionInfo(executable);
        if (version.ProductName?.Contains("Afterburner", StringComparison.OrdinalIgnoreCase) != true)
        {
            throw new InvalidDataException("The selected executable does not identify itself as MSI Afterburner.");
        }

        var plugin = Path.Combine(directory, "Plugins", "Monitoring", "PSU.dll");
        var config = AfterburnerConfiguration.FileNames.All(name => File.Exists(Path.Combine(directory, name)));
        return new(directory, version.FileVersion ?? "Unknown", File.Exists(plugin), config);
    }

    public static bool IsSupportedVersion(string fileVersion) =>
        Version.TryParse(fileVersion, out var version) && version >= new Version(4, 6, 7);

    public static void RequireSupported(AfterburnerInstallation installation, bool requireFiles = true)
    {
        if (!IsSupportedVersion(installation.Version))
        {
            throw new InvalidOperationException("Safeguard+ configuration requires Afterburner 4.6.7 or newer.");
        }

        if (requireFiles && (!installation.PluginPresent || !installation.ConfigPresent))
        {
            throw new InvalidOperationException("PSU.dll and both configuration files must exist. Configure Afterburner once first.");
        }
    }

    public static bool IsRunning()
    {
        var processes = Process.GetProcessesByName("MSIAfterburner");
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    public static void RequireStopped()
    {
        var processes = Process.GetProcessesByName("MSIAfterburner");
        try
        {
            if (processes.Length != 0)
            {
                throw new InvalidOperationException("Close Afterburner, including its tray icon, before saving a backup, applying or restoring configuration.");
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}