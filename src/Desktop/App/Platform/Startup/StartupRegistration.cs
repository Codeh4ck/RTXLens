using System.Security.Principal;
using Microsoft.Win32;
using RTXLens.Installer.Actions.Startup;

namespace RTXLens.App.Platform.Startup;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class StartupRegistration
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "RTXLens";

    public static void SetEnabled(bool enabled)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new IOException("The Windows account could not be identified.");
        var executable = Path.Combine(AppContext.BaseDirectory, "RTXLens.App.exe");
        if (enabled)
        {
            ElevatedStartupTask.Register(sid, executable);
        }
        else
        {
            ElevatedStartupTask.Remove(sid, executable);
        }

        using var key = Registry.CurrentUser.OpenSubKey(Key, writable: true);
        if (key?.GetValue(Name) is string command
            && (string.Equals(command, $"\"{executable}\"", StringComparison.OrdinalIgnoreCase)
                || command.StartsWith($"\"{executable}\" ", StringComparison.OrdinalIgnoreCase)))
        {
            key.DeleteValue(Name, throwOnMissingValue: false);
        }
    }
}