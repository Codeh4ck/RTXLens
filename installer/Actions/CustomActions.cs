using System;
using Microsoft.Win32;
using RTXLens.Installer.Actions.Configuration;
using RTXLens.Installer.Actions.Startup;
using RTXLens.Installer.Actions.Services;
using RTXLens.Installer.Actions.Validation;
using WixToolset.Dtf.WindowsInstaller;

namespace RTXLens.Installer.Actions;

public static class CustomActions
{
    [CustomAction]
    public static ActionResult Preflight(Session session) => Run(session, () =>
    {
        InstallationPreflight.CheckDesktopClosed(session["INSTALLFOLDER"]);
        var installed = !string.IsNullOrEmpty(session["Installed"]);
        var destination = session["INSTALLFOLDER"];

        using var hashes = session.Database.OpenView("SELECT `Data` FROM `Binary` WHERE `Name`='NotifierFileHashes'");
        hashes.Execute();

        using var record = hashes.Fetch();
        using var stream = record.GetStream(1);

        InstallationPreflight.CheckServiceOwnership(destination, installed, InstallationPreflight.ReadNotifierHashes(stream));
    });

    [CustomAction]
    public static ActionResult PortAvailable(Session session) => Run(session, InstallationPreflight.CheckPortAvailable);

    [CustomAction]
    public static ActionResult PrepareConfigurationBackup(Session session) => Run(session, () =>
    {
        var data = new CustomActionData { { "BackupId", Guid.NewGuid().ToString("N") } };
        foreach (var action in new[] { "BackupConfiguration", "RestoreConfiguration", "CleanConfigurationBackup" })
        {
            session[action] = data.ToString();
        }
    });

    [CustomAction]
    public static ActionResult BackupConfiguration(Session session) => Run(session, () => new ConfigurationBackup(session.CustomActionData["BackupId"]).Capture());

    [CustomAction]
    public static ActionResult RestoreConfiguration(Session session) => Run(session, () => new ConfigurationBackup(session.CustomActionData["BackupId"]).Restore());

    [CustomAction]
    public static ActionResult CleanConfigurationBackup(Session session) => Run(session, () => new ConfigurationBackup(session.CustomActionData["BackupId"]).Clean());

    [CustomAction]
    public static ActionResult PrepareServiceHealth(Session session) => Run(session, () =>
    {
        session["VerifyServiceHealth"] = new CustomActionData { { "Version", session["ProductVersion"] } }.ToString();
    });

    [CustomAction]
    public static ActionResult VerifyServiceHealth(Session session) => Run(session, () => ServiceHealth.Wait(session.CustomActionData["Version"]));

    [CustomAction]
    public static ActionResult ConfigureServiceStartup(Session session) => Run(session, ServiceStartupConfiguration.Apply);

    [CustomAction]
    public static ActionResult PrepareStartupCleanup(Session session) => Run(session, () =>
    {
        session["RemoveStartupRegistration"] = new CustomActionData { { "InstallDirectory", session["INSTALLFOLDER"] } }.ToString();
    });

    [CustomAction]
    public static ActionResult RemoveStartupRegistration(Session session) => Run(session, () => StartupCleanup.Remove(session.CustomActionData["InstallDirectory"], session.Log));

    [CustomAction]
    public static ActionResult PrepareStartupMigration(Session session) => Run(session, () =>
    {
        session["MigrateStartupRegistration"] = new CustomActionData
        {
            { "InstallDirectory", session["INSTALLFOLDER"] }
        }.ToString();
    });

    [CustomAction]
    public static ActionResult MigrateStartupRegistration(Session session) => Run(session, () =>
        StartupCleanup.Migrate(session.CustomActionData["InstallDirectory"], session.Log));

    [CustomAction]
    public static ActionResult PrepareAfterburnerBackupCleanup(Session session) => Run(session, () =>
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var registration = hive.OpenSubKey(@"SOFTWARE\RTXLens");

        var installation = registration?.GetValue("InstallDirectory") as string;
        session["RemoveAfterburnerBackups"] = new CustomActionData { { "InstallDirectory", installation ?? "" } }.ToString();
    });

    [CustomAction]
    public static ActionResult RemoveAfterburnerBackups(Session session) => Run(session, () =>
        AfterburnerBackupCleanup.Remove(session.CustomActionData["InstallDirectory"],
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), session.Log));

    private static ActionResult Run(Session session, Action action)
    {
        try
        {
            action();
            return ActionResult.Success;
        }
        catch (Exception error)
        {
            session.Log(error.ToString());

            using var record = new Record(1);
            record[0] = "RTX Lens setup: [1]";
            record[1] = error.Message;

            session.Message(InstallMessage.Error, record);
            return ActionResult.Failure;
        }
    }
}
