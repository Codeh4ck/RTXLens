using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RTXLens.Presentation.Msi.Afterburner;

public sealed partial class AfterburnerFileStore(string installation, string backupRoot, string? legacyBackupDirectory = null)
{
    private const int MaximumConfigBytes = 4 * 1024 * 1024;
    public string Installation { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installation));
    public string BackupDirectory { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(backupRoot));
    private readonly string? _legacyBackupDirectory = legacyBackupDirectory is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyBackupDirectory));

    public static string DefaultBackupRoot => Path.Combine(AppContext.BaseDirectory, "AfterburnerBackups");

    public static string LegacyBackupDirectoryFor(string installation) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RTXLens.AfterburnerBackups", InstallationKey(installation));

    public bool HasInterruptedChange => File.Exists(Path.Combine(BackupDirectory, "pending.json")) || LegacyStore?.HasInterruptedChange == true;

    public AfterburnerChangeSet Read(bool validate = true)
    {
        var files = AfterburnerConfiguration.FileNames.Select(name =>
        {
            var path = Target(name);
            var exists = File.Exists(path);
            var bytes = !validate && !exists ? [] : ReadLimited(path);
            return new AfterburnerFileChange(name, bytes, bytes) { BeforeExists = exists };
        }).ToArray();
        if (validate)
        {
            _ = AfterburnerConfiguration.Inspect(files[0].Before, files[1].Before);
        }

        return new(Installation, files);
    }

    public AfterburnerBackup[] Backups()
    {
        EnsureNoLinks(BackupDirectory);
        if (!Directory.Exists(BackupDirectory))
        {
            return LegacyStore?.Backups() ?? [];
        }

        var backups = new List<AfterburnerBackup>();
        foreach (var directory in Directory.EnumerateDirectories(BackupDirectory))
        {
            if (!BackupId().IsMatch(Path.GetFileName(directory)))
            {
                continue;
            }

            try
            {
                var backup = ReadBackup(Path.GetFileName(directory));
                if (backup.Restorable)
                {
                    backups.Add(backup);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
            {
                // Incomplete or invalid backups are never offered for restoration.
            }
        }

        if (LegacyStore is { } legacy)
        {
            backups.AddRange(legacy.Backups().Where(item => !Directory.Exists(Path.Combine(BackupDirectory, item.Id))));
        }

        return [.. backups.OrderByDescending(item => item.CreatedAt)];
    }

    public string BackupFingerprint(string id) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(ReadBackup(id), AfterburnerJsonContext.Default.AfterburnerBackup)));

    public void DeleteBackup(string id, string expectedFingerprint) =>
        DeleteBackups([new(id, expectedFingerprint)]);

    public void DeleteBackups(IReadOnlyList<AfterburnerBackupDeletion> selections)
    {
        if (selections.Count == 0 || selections.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != selections.Count)
        {
            throw new ArgumentException("Select one or more distinct backups to delete.");
        }

        // Inspect the entire selection before deleting anything, including recovery-journal and file-integrity checks.
        var directories = selections.Select(item => ValidateBackupDeletion(item.Id, item.ExpectedFingerprint)).ToArray();
        var deleted = 0;
        try
        {
            foreach (var directory in directories)
            {
                DeleteBackupFiles(directory);
                deleted++;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException(deleted + " of " + selections.Count + " backups were deleted. " + error.Message, error);
        }
    }

    private string ValidateBackupDeletion(string id, string expectedFingerprint)
    {
        var directory = BackupPath(id);
        if (!Path.GetDirectoryName(Path.GetFullPath(directory))!.Equals(BackupDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The selected backup is outside the backup directory.");
        }

        if (BackupFingerprint(id) != expectedFingerprint)
        {
            throw new InvalidOperationException("The selected backup changed since it was inspected. Refresh before deleting it.");
        }

        var journal = Path.Combine(BackupDirectory, "pending.json");
        EnsureNoLinks(journal);
        if (File.Exists(journal) && File.ReadAllText(journal).Trim() == id)
        {
            throw new InvalidOperationException("This backup is needed to recover an interrupted update. Complete recovery before deleting it.");
        }

        var allowed = new[] { "PSU.cfg", "MSIAfterburner.cfg", "manifest.json" };
        if (Directory.EnumerateDirectories(directory).Any()
            || Directory.EnumerateFiles(directory).Any(path => !allowed.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("The backup contains unexpected files and was not deleted.");
        }

        foreach (var name in allowed)
        {
            EnsureNoLinks(Path.Combine(directory, name));
        }

        return directory;
    }

    private static void DeleteBackupFiles(string directory)
    {
        var allowed = new[] { "PSU.cfg", "MSIAfterburner.cfg", "manifest.json" };
        // Delete only the known files inside this one validated backup. Never recurse over a supplied path.
        foreach (var name in allowed)
        {
            var path = Path.Combine(directory, name);
            EnsureNoLinks(path);
            File.Delete(path);
        }

        Directory.Delete(directory, false);
    }

    public AfterburnerBackup Save(AfterburnerChangeSet expected, string kind = "Saved working configuration")
    {
        ValidateCurrent(expected);
        EnsureNoLinks(BackupDirectory);
        var restorable = expected.Files.All(file => file.BeforeExists);
        try
        {
            _ = AfterburnerConfiguration.Inspect(expected.Files[0].Before, expected.Files[1].Before);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException)
        {
            restorable = false;
        }

        var backup = new AfterburnerBackup(DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow, kind, Installation, expected.Files.ToDictionary(file => file.RelativePath, file => file.BeforeHash), restorable);
        var directory = BackupPath(backup.Id);
        Directory.CreateDirectory(directory);

        foreach (var file in expected.Files)
        {
            WriteNew(Path.Combine(directory, Path.GetFileName(file.RelativePath)), file.Before);
        }

        WriteNew(Path.Combine(directory, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(backup, AfterburnerJsonContext.Default.AfterburnerBackup));
        _ = ReadBackup(backup.Id);
        return backup;
    }

    public AfterburnerChangeSet PlanRestore(string id)
    {
        var backup = ReadBackup(id);
        if (!backup.Restorable)
        {
            throw new InvalidDataException("This archive contains an incomplete or invalid previous configuration and cannot be restored automatically.");
        }

        var current = Read(false);
        var directory = BackupPath(backup.Id);
        var files = current.Files.Select(file => file with { After = ReadLimited(Path.Combine(directory, Path.GetFileName(file.RelativePath))) }).ToArray();
        _ = AfterburnerConfiguration.Inspect(files[0].After, files[1].After);
        return new(Installation, files);
    }

    public AfterburnerBackup Apply(AfterburnerChangeSet plan, bool restoring = false, Action<string>? beforeReplace = null)
    {
        ValidateCurrent(plan);
        if (HasInterruptedChange && !restoring)
        {
            throw new InvalidOperationException("An interrupted configuration update needs attention. Restore a saved backup before applying new settings.");
        }

        var backup = Save(plan, restoring ? "Before restore" : "Before apply");
        var journal = Path.Combine(BackupDirectory, "pending.json");
        File.WriteAllText(journal, backup.Id);
        var replaced = new List<AfterburnerFileChange>();

        try
        {
            foreach (var file in plan.Files.Where(file => file.Changed))
            {
                beforeReplace?.Invoke(file.RelativePath);
                RequireBefore(file);
                Replace(Target(file.RelativePath), file.After);
                replaced.Add(file);
            }

            foreach (var file in plan.Files)
            {
                RequireHash(Target(file.RelativePath), file.AfterHash);
            }

            _ = Read();
            File.Delete(journal);
            return backup;
        }
        catch (Exception failure)
        {
            try
            {
                foreach (var file in replaced.AsEnumerable().Reverse())
                {
                    // Do not overwrite a concurrent external edit while rolling back.
                    RequireHash(Target(file.RelativePath), file.AfterHash);
                    if (file.BeforeExists)
                    {
                        Replace(Target(file.RelativePath), file.Before);
                    }
                    else
                    {
                        File.Delete(Target(file.RelativePath));
                    }
                }

                foreach (var file in plan.Files)
                {
                    RequireBefore(file);
                }

                File.Delete(journal);
            }
            catch (Exception rollbackFailure)
            {
                throw new IOException("Configuration update failed and automatic rollback could not finish. Afterburner must remain closed. Restore backup "
                    + backup.Id + ".", new AggregateException(failure, rollbackFailure));
            }

            throw new IOException("No changes were kept. Configuration update failed; previous contents were restored. Backup: " + backup.Id, failure);
        }
    }

    public void ValidateCurrent(AfterburnerChangeSet plan)
    {
        if (!Installation.Equals(plan.Installation, StringComparison.OrdinalIgnoreCase)
            || plan.Files.Length != 2
            || !plan.Files.Select(file => file.RelativePath).SequenceEqual(AfterburnerConfiguration.FileNames))
        {
            throw new InvalidDataException("The configuration plan does not match the selected installation and file pair.");
        }

        foreach (var file in plan.Files)
        {
            RequireBefore(file);
        }

        _ = AfterburnerConfiguration.Inspect(plan.Files[0].After, plan.Files[1].After);
    }

    private AfterburnerBackup ReadBackup(string id, string? directory = null)
    {
        if (!BackupId().IsMatch(id))
        {
            throw new InvalidDataException("Invalid backup identifier.");
        }

        directory ??= BackupPath(id);
        EnsureNoLinks(directory);
        var manifest = JsonSerializer.Deserialize(ReadLimited(Path.Combine(directory, "manifest.json")),
            AfterburnerJsonContext.Default.AfterburnerBackup) ?? throw new InvalidDataException("Backup manifest is empty.");

        if (manifest.Id != id || !Installation.Equals(manifest.Installation, StringComparison.OrdinalIgnoreCase)
            || manifest.Hashes is null || manifest.Hashes.Count != 2 || !AfterburnerConfiguration.FileNames.All(manifest.Hashes.ContainsKey))
        {
            throw new InvalidDataException("The backup belongs to another installation or has an invalid file list.");
        }

        foreach (var file in AfterburnerConfiguration.FileNames)
        {
            RequireHash(Path.Combine(directory, Path.GetFileName(file)), manifest.Hashes[file]);
        }

        if (manifest.Restorable)
        {
            _ = AfterburnerConfiguration.Inspect(ReadLimited(Path.Combine(directory, "PSU.cfg")),
                ReadLimited(Path.Combine(directory, "MSIAfterburner.cfg")));
        }
        return manifest;
    }

    private string Target(string name)
    {
        if (!AfterburnerConfiguration.FileNames.Contains(name))
        {
            throw new InvalidDataException("Unsupported configuration file.");
        }

        var path = Path.Combine(Installation, name);
        EnsureNoLinks(path);
        return path;
    }

    private string BackupPath(string id)
    {
        if (!BackupId().IsMatch(id))
        {
            throw new InvalidDataException("Invalid backup identifier.");
        }

        var path = Path.Combine(BackupDirectory, id);
        EnsureNoLinks(path);
        if (!Directory.Exists(path) && LegacyStore is { } legacy && Directory.Exists(Path.Combine(legacy.BackupDirectory, id)))
        {
            return legacy.BackupPath(id);
        }

        return path;
    }

    private static byte[] ReadLimited(string path)
    {
        EnsureNoLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumConfigBytes)
        {
            throw new InvalidDataException("Configuration or backup file is unexpectedly large.");
        }

        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private void RequireBefore(AfterburnerFileChange file)
    {
        var path = Target(file.RelativePath);
        if (!file.BeforeExists)
        {
            if (File.Exists(path))
            {
                throw new InvalidOperationException("A previously missing configuration file was created externally. Refresh and inspect a new diff.");
            }

            return;
        }

        RequireHash(path, file.BeforeHash);
    }

    private static void RequireHash(string path, string expected)
    {
        if (Convert.ToHexString(SHA256.HashData(ReadLimited(path))) != expected)
        {
            throw new InvalidOperationException("Configuration changed since it was inspected, or backup verification failed. Refresh and inspect the changes again.");
        }
    }

    private static void WriteNew(string path, byte[] contents)
    {
        EnsureNoLinks(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(contents);
        stream.Flush(true);
    }

    private static void Replace(string path, byte[] contents)
    {
        var temporary = path + ".rtx-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteNew(temporary, contents);
            if (File.Exists(path))
            {
                File.Replace(temporary, path, null);
            }
            else
            {
                File.Move(temporary, path);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static void EnsureNoLinks(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Configuration and backup paths must not contain symbolic links or junctions.");
            }

            current = Path.GetDirectoryName(current);
        }
    }

    [GeneratedRegex(@"\A\d{8}-\d{6}-\d{3}-[0-9a-f]{32}\z")]
    private static partial Regex BackupId();
}