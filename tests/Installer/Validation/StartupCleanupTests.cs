using System;
using Microsoft.Win32;
using RTXLens.Installer.Actions.Startup;

namespace RTXLens.Installer.Tests.Validation;

internal static class StartupCleanupTests
{
    public static void Run()
    {
        const string parent = @"Software\RTXLens.Installer.Tests\";
        var id = Guid.NewGuid().ToString("N");
        var path = parent + id;
        const string runPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string approvedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        const string directory = @"C:\Program Files\RTXLens";
        using var hive = Registry.CurrentUser.CreateSubKey(path, true);
        try
        {
            using var run = hive.CreateSubKey(runPath, true);
            using var approved = hive.CreateSubKey(approvedPath, true);
            run.SetValue("RTXLens", '"' + directory + "\\Desktop\\RTXLens.App.exe\" --startup");
            run.SetValue("AnotherApplication", "preserve");
            approved.SetValue("RTXLens", new byte[] { 2, 0, 0, 0 });
            Clean(hive, directory);
            Check.That(run.GetValue("RTXLens") is null, "Uninstall removes the installed-path Run entry in an isolated test hive.");
            Check.That(approved.GetValue("RTXLens") is null, "Uninstall removes only its matching StartupApproved entry.");
            Check.That((string)run.GetValue("AnotherApplication") == "preserve", "Uninstall preserves other startup applications.");
            run.SetValue("RTXLens", "\"C:\\Portable\\RTXLens.App.exe\" --startup");
            approved.SetValue("RTXLens", new byte[] { 2, 0, 0, 0 });
            Clean(hive, directory);
            Check.That(run.GetValue("RTXLens") is not null && approved.GetValue("RTXLens") is not null,
                "Portable startup registration and approval are retained when the installed app is removed.");
        }
        finally
        {
            hive.Dispose();
            if (!path.StartsWith(parent, StringComparison.Ordinal) || !Guid.TryParseExact(id, "N", out _))
            {
                throw new InvalidOperationException("Invalid isolated registry cleanup path.");
            }

            Registry.CurrentUser.DeleteSubKeyTree(path, false);
        }
    }

    private static void Clean(RegistryKey hive, string directory) => StartupCleanup.UpdateHive(hive, directory, "", migrate: false);
}
