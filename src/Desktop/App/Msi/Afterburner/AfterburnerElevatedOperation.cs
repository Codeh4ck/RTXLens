using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using RTXLens.Presentation.Msi.Afterburner;

namespace RTXLens.App.Msi.Afterburner;

// This entry point edits validated INI files or requests a normal exit of the selected Afterburner installation. It never runs alarm commands.
internal static class AfterburnerElevatedOperation
{
    private static string ResultsDirectory => Path.Combine(AfterburnerFileStore.DefaultBackupRoot, "OperationResults");

    internal static async Task<AfterburnerOperationResult> ExecuteAsync(AfterburnerOperation operation)
    {
        var id = Guid.NewGuid().ToString("N");
        var requests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RTXLens", "AfterburnerRequests");
        Directory.CreateDirectory(requests);
        var request = Path.Combine(requests, id + ".json");

        try
        {
            var requestBytes = JsonSerializer.SerializeToUtf8Bytes(operation, AfterburnerJsonContext.Default.AfterburnerOperation);
            await File.WriteAllBytesAsync(request, requestBytes);
            var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("App executable path is unavailable."))
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            start.ArgumentList.Add("--afterburner-config");
            start.ArgumentList.Add(id);
            start.ArgumentList.Add(request);
            start.ArgumentList.Add(Convert.ToHexString(SHA256.HashData(requestBytes)));

            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the elevated configuration helper.");
            await process.WaitForExitAsync();
            var resultPath = ResultPath(id);
            AfterburnerFileStore.EnsureNoLinks(resultPath);

            if (!File.Exists(resultPath))
            {
                throw new InvalidOperationException("The configuration helper did not return a result. Refresh and inspect the files before retrying.");
            }

            return JsonSerializer.Deserialize(await File.ReadAllBytesAsync(resultPath),
                AfterburnerJsonContext.Default.AfterburnerOperationResult)
                ?? throw new InvalidDataException("The configuration helper returned an empty result.");
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            return new(false, "Administrator approval was cancelled. No configuration changes were made.");
        }
        finally
        {
            File.Delete(request);
        }
    }

    internal static async Task RunHelperAsync(string id, string requestPath, string expectedRequestHash)
    {
        if (!Guid.TryParseExact(id, "N", out _))
        {
            Environment.ExitCode = 1;
            return;
        }

        AfterburnerOperationResult result;
        try
        {
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            {
                throw new UnauthorizedAccessException("Administrator access is required.");
            }

            ProtectBackupRoot();
            AfterburnerFileStore.EnsureNoLinks(requestPath);
            if (new FileInfo(requestPath).Length > 64 * 1024)
            {
                throw new InvalidDataException("The configuration request is unexpectedly large.");
            }

            var bytes = await File.ReadAllBytesAsync(requestPath);
            if (bytes.Length > 64 * 1024 || Convert.ToHexString(SHA256.HashData(bytes)) != expectedRequestHash)
            {
                throw new InvalidDataException("The configuration request changed after it was approved. Inspect a new diff.");
            }

            var operation = JsonSerializer.Deserialize(bytes, AfterburnerJsonContext.Default.AfterburnerOperation)
                ?? throw new InvalidDataException("Configuration request is empty.");
            var installation = AfterburnerDiscovery.Inspect(operation.Installation);
            AfterburnerDiscovery.RequireSupported(installation, operation.Action is "save" or "apply" or "stop-for-restart");
            if (operation.Action is not ("delete" or "stop-for-restart"))
            {
                AfterburnerDiscovery.RequireStopped();
            }
            var store = new AfterburnerFileStore(installation.Directory, AfterburnerFileStore.DefaultBackupRoot, AfterburnerFileStore.LegacyBackupDirectoryFor(installation.Directory));
            var lockPath = Path.Combine(AfterburnerFileStore.DefaultBackupRoot, ".operation.lock");
            AfterburnerFileStore.EnsureNoLinks(lockPath);
            using var operationLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            store.ImportLegacyBackups();
            if (operation.Action == "stop-for-restart")
            {
                if (store.HasInterruptedChange || store.Read().Id != operation.ExpectedPlanId)
                {
                    throw new InvalidOperationException("Configuration changed or requires recovery. Refresh its status before restarting Afterburner.");
                }

                await AfterburnerProcessControl.CloseAsync(installation.Directory);
                result = new(true, "Afterburner exited normally. The desktop app can now relaunch it.");
            }
            else if (operation.Action == "delete")
            {
                var selections = operation.BackupsToDelete.Length > 0 ? operation.BackupsToDelete
                    : operation.BackupId is { } backupId ? new[] { new AfterburnerBackupDeletion(backupId, operation.ExpectedPlanId) }
                    : throw new InvalidDataException("Select the backups to delete.");
                store.DeleteBackups(selections);
                result = new(true, selections.Length == 1
                    ? "Backup deleted. Afterburner configuration was not changed."
                    : selections.Length + " backups deleted. Afterburner configuration was not changed.");
            }
            else
            {
                var current = store.Read(operation.Action != "restore");
                var plan = operation.Action switch
                {
                    "save" => current,
                    "apply" when operation.Settings is not null => AfterburnerConfiguration.Plan(installation.Directory,
                        current.Files[0].Before, current.Files[1].Before, operation.Settings),
                    "restore" when operation.BackupId is not null => store.PlanRestore(operation.BackupId),
                    _ => throw new InvalidDataException("Unsupported configuration operation.")
                };

                if (plan.Id != operation.ExpectedPlanId)
                {
                    throw new InvalidOperationException("The files or proposed changes no longer match the inspected version. Refresh and inspect a new diff.");
                }

                if (operation.Action == "save")
                {
                    var backup = store.Save(plan);
                    result = new(true, "Working configuration saved: " + backup.Id);
                }
                else
                {
                    // All checks, including process detection, are repeated in the elevated process before writing.
                    AfterburnerDiscovery.RequireStopped();
                    if (operation is { Action: "apply", Settings: { } settings })
                    {
                        foreach (var alarm in settings.GetConnectorAlarms())
                        {
                            if (alarm.Script is { } script)
                            {
                                await AfterburnerSafeguard.InstallBindingAsync(installation.Directory, alarm.Connector, script);
                            }
                        }
                    }

                    AfterburnerDiscovery.RequireStopped();
                    var backup = store.Apply(plan, operation.Action == "restore");
                    File.WriteAllText(Path.Combine(store.BackupDirectory, "restart-required.txt"), DateTimeOffset.UtcNow.ToString("O"));
                    result = new(true, (operation.Action == "restore" ? "Configuration restored" : "Configuration saved")
                        + " - Afterburner restart required. Previous configuration backup: " + backup.Id);
                }
            }
        }
        catch (Exception error)
        {
            Environment.ExitCode = 1;
            result = new(false, error.Message);
        }

        try
        {
            AfterburnerFileStore.EnsureNoLinks(ResultsDirectory);
            Directory.CreateDirectory(ResultsDirectory);
            var path = ResultPath(id);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(stream, result, AfterburnerJsonContext.Default.AfterburnerOperationResult);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Environment.ExitCode = 1;
            Debug.WriteLine(error);
        }
    }

    private static string ResultPath(string id) => Path.Combine(ResultsDirectory, id + ".json");

    private static void ProtectBackupRoot()
    {
        var root = AfterburnerFileStore.DefaultBackupRoot;
        AfterburnerFileStore.EnsureNoLinks(root);
        var existed = Directory.Exists(root);
        var directory = Directory.CreateDirectory(root);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        if (existed)
        {
            var owner = directory.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
            if (!Equals(owner, administrators) && !Equals(owner, system))
            {
                throw new UnauthorizedAccessException("The backup directory has an unexpected owner. An administrator must inspect it before configuration can be saved.");
            }
        }

        var security = new DirectorySecurity();
        security.SetOwner(administrators);
        security.SetAccessRuleProtection(true, false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl, inheritance,
            PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inheritance,
            PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, inheritance,
            PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);
    }
}