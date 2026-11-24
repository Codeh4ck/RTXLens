using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace RTXLens.Installer.Actions.Startup;

public static class StartupCleanup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RTXLens";

    public static bool MatchesInstalledApp(string? command, string installDirectory)
    {
        var executable = Path.Combine(Path.GetFullPath(installDirectory), "Desktop", "RTXLens.App.exe");

        return command is not null && (command.Equals('"' + executable + '"', StringComparison.OrdinalIgnoreCase)
                                       || command.StartsWith('"' + executable + "\" ", StringComparison.OrdinalIgnoreCase));
    }

    public static void Remove(string installDirectory, Action<string> log) => Update(installDirectory, log, migrate: false);
    public static void Migrate(string installDirectory, Action<string> log) => Update(installDirectory, log, migrate: true);

    private static void Update(string installDirectory, Action<string> log, bool migrate)
    {
        var loaded = new HashSet<string>(Registry.Users.GetSubKeyNames(), StringComparer.OrdinalIgnoreCase);
        using var profiles = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");

        foreach (var sid in profiles?.GetSubKeyNames() ?? [])
        {
            if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) && !sid.StartsWith("S-1-12-1-", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                if (!migrate)
                {
                    ElevatedStartupTask.Remove(sid, Path.Combine(installDirectory, "Desktop", "RTXLens.App.exe"));
                }

                if (loaded.Contains(sid))
                {
                    using var hive = Registry.Users.OpenSubKey(sid, true);
                    UpdateHive(hive, installDirectory, sid, migrate);
                }
                else
                {
                    using var profile = profiles!.OpenSubKey(sid);
                    if (profile?.GetValue("ProfileImagePath") is not string directory)
                    {
                        continue;
                    }

                    var file = Path.Combine(Environment.ExpandEnvironmentVariables(directory), "NTUSER.DAT");
                    if (!File.Exists(file))
                    {
                        continue;
                    }

                    var result = RegLoadAppKey(file, out var handle, 0x20019 | 0x20006, 1, 0);
                    using (handle)
                    {
                        if (result != 0)
                        {
                            log($"Could not inspect startup registration for {sid} (Windows error {result}).");
                            continue;
                        }

                        using var hive = RegistryKey.FromHandle(handle);
                        UpdateHive(hive, installDirectory, sid, migrate);
                    }
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is System.Security.SecurityException || error is COMException)
            {
                log($"Could not remove startup registration for {sid}: {error.Message}");
            }
        }
    }

    internal static void UpdateHive(RegistryKey? hive, string installDirectory, string sid, bool migrate)
    {
        using var run = hive?.OpenSubKey(RunKey, true);

        if (!MatchesInstalledApp(run?.GetValue(ValueName) as string, installDirectory))
        {
            return;
        }

        if (migrate)
        {
            ElevatedStartupTask.Register(sid, Path.Combine(installDirectory, "Desktop", "RTXLens.App.exe"));
        }

        run!.DeleteValue(ValueName, false);

        using var approved = hive?.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", true);
        approved?.DeleteValue(ValueName, false);
    }

    [DllImport("advapi32.dll", EntryPoint = "RegLoadAppKeyW", CharSet = CharSet.Unicode)]
    private static extern int RegLoadAppKey(string file, out SafeRegistryHandle handle, int access, int options, int reserved);
}
