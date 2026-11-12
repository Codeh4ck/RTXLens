using System;
using System.IO;
using System.Runtime.InteropServices;
using RTXLens.Contracts.Security;

namespace RTXLens.Installer.Actions.Startup;

#if !NETFRAMEWORK
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
internal static class ElevatedStartupTask
{
    private const int TaskNotFound = unchecked((int)0x80070002);

    public static void Register(string sid, string executable)
    {
        ProtectedInstallationFile.Require(executable);
        dynamic scheduler = Connect();
        dynamic folder = scheduler.GetFolder(@"\");
        dynamic task = scheduler.NewTask(0);
        task.RegistrationInfo.Description = "Start RTX Lens at sign-in with administrator privileges.";
        task.Principal.UserId = sid;
        task.Principal.LogonType = 3; // Interactive token.
        task.Principal.RunLevel = 1; // Highest available.
        task.Settings.DisallowStartIfOnBatteries = false;
        task.Settings.StopIfGoingOnBatteries = false;
        task.Settings.ExecutionTimeLimit = "PT0S";
        task.Settings.MultipleInstances = 2; // Keep the running instance.
        dynamic trigger = task.Triggers.Create(9); // Logon.
        trigger.UserId = sid;
        dynamic action = task.Actions.Create(0); // Executable.
        action.Path = executable;
        action.Arguments = "--startup";
        action.WorkingDirectory = Path.GetDirectoryName(executable);
        folder.RegisterTaskDefinition(Name(sid), task, 6, sid, null, 3);
    }

    public static void Remove(string sid, string executable)
    {
        dynamic scheduler = Connect();
        dynamic folder = scheduler.GetFolder(@"\");
        try
        {
            dynamic task = folder.GetTask(Name(sid));
            dynamic actions = task.Definition.Actions;
            if ((int)actions.Count == 1)
            {
                dynamic action = actions.Item(1);
                if (string.Equals((string)action.Path, executable, StringComparison.OrdinalIgnoreCase)
                    && string.Equals((string)action.Arguments, "--startup", StringComparison.Ordinal))
                {
                    folder.DeleteTask(Name(sid), 0);
                }
            }
        }
        catch (COMException error) when (error.HResult == TaskNotFound)
        {
            // This user has no registered RTX Lens startup task.
        }
    }

    public static void Migrate(string sid, string previousExecutable, string executable)
    {
        dynamic scheduler = Connect();
        dynamic folder = scheduler.GetFolder(@"\");
        var previousName = "RTX Monitor Startup " + sid;
        try
        {
            dynamic task = folder.GetTask(previousName);
            dynamic actions = task.Definition.Actions;
            if ((int)actions.Count != 1)
            {
                return;
            }

            dynamic action = actions.Item(1);
            if (!string.Equals((string)action.Path, previousExecutable, StringComparison.OrdinalIgnoreCase)
                || !string.Equals((string)action.Arguments, "--startup", StringComparison.Ordinal))
            {
                return;
            }

            if ((bool)task.Enabled)
            {
                Register(sid, executable);
            }

            folder.DeleteTask(previousName, 0);
        }
        catch (COMException error) when (error.HResult == TaskNotFound)
        {
            // No previous product task for this user.
        }
    }

    private static object Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new IOException("Windows Task Scheduler is unavailable.");
        dynamic scheduler = Activator.CreateInstance(type) ?? throw new IOException("Windows Task Scheduler could not start.");
        scheduler.Connect();
        return scheduler;
    }

    private static string Name(string sid) => "RTX Lens Startup " + sid;
}