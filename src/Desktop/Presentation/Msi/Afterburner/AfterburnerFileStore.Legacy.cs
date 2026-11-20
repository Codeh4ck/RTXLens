using RTXLens.Plugins.Msi.Contracts;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RTXLens.Presentation.Msi.Afterburner;

public sealed partial class AfterburnerFileStore
{
    private static readonly string[] BackupFiles = [AfterburnerFiles.PluginConfig, AfterburnerFiles.ProfileConfig, AfterburnerFiles.BackupManifest];
    private string LegacyImportMarker => Path.Combine(BackupDirectory, "legacy-import-" + InstallationKey(Installation) + ".txt");

    private AfterburnerFileStore? LegacyStore
    {
        get
        {
            if (_legacyBackupDirectory is null || _legacyBackupDirectory.Equals(BackupDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            EnsureNoLinks(LegacyImportMarker);
            return File.Exists(LegacyImportMarker) ? null : new AfterburnerFileStore(Installation, _legacyBackupDirectory);
        }
    }

    private static string InstallationKey(string installation) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(installation)).ToUpperInvariant())))[..20];

    // Called under the elevated operation lock. Originals remain untouched until the new copies are verified.
    public int ImportLegacyBackups()
    {
        if (LegacyStore is not { } legacy || !Directory.Exists(legacy.BackupDirectory))
        {
            return 0;
        }

        EnsureNoLinks(legacy.BackupDirectory);
        EnsureNoLinks(BackupDirectory);
        Directory.CreateDirectory(BackupDirectory);
        var imported = 0;
        foreach (var source in Directory.EnumerateDirectories(legacy.BackupDirectory))
        {
            var id = Path.GetFileName(source);
            if (!BackupId().IsMatch(id))
            {
                continue;
            }

            try
            {
                _ = legacy.ReadBackup(id);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
            {
                continue; // Invalid legacy snapshots stay at their original location and are not imported as trusted backups.
            }

            var destination = Path.Combine(BackupDirectory, id);
            EnsureNoLinks(destination);
            if (Directory.Exists(destination))
            {
                _ = ReadBackup(id, destination);
                if (BackupFiles.Any(name => !ReadLimited(Path.Combine(source, name)).AsSpan().SequenceEqual(ReadLimited(Path.Combine(destination, name)))))
                {
                    throw new InvalidDataException("A different backup already uses the legacy backup identifier: " + id);
                }

                continue;
            }

            var staging = Path.Combine(BackupDirectory, ".import-" + Guid.NewGuid().ToString("N"));
            EnsureNoLinks(staging);
            Directory.CreateDirectory(staging);
            try
            {
                foreach (var name in BackupFiles)
                {
                    File.WriteAllBytes(Path.Combine(staging, name), ReadLimited(Path.Combine(source, name)));
                }

                _ = ReadBackup(id, staging);
                Directory.Move(staging, destination);
                imported++;
            }
            finally
            {
                // Clean only the known temporary files. Never recursively delete a supplied directory.
                if (Directory.Exists(staging))
                {
                    foreach (var name in BackupFiles)
                    {
                        var path = Path.Combine(staging, name);
                        EnsureNoLinks(path);
                        File.Delete(path);
                    }

                    Directory.Delete(staging, false);
                }
            }
        }

        var legacyJournal = Path.Combine(legacy.BackupDirectory, "pending.json");
        EnsureNoLinks(legacyJournal);
        if (File.Exists(legacyJournal))
        {
            var pendingBytes = ReadLimited(legacyJournal);
            // Keep even an incomplete journal as a recovery flag; a verified working snapshot can still restore it.
            var journal = Path.Combine(BackupDirectory, "pending.json");
            EnsureNoLinks(journal);
            if (File.Exists(journal) && !ReadLimited(journal).AsSpan().SequenceEqual(pendingBytes))
            {
                throw new InvalidOperationException("Complete recovery of the current backup store before importing an interrupted legacy change.");
            }

            File.WriteAllBytes(journal, pendingBytes);
        }

        EnsureNoLinks(LegacyImportMarker);
        File.WriteAllText(LegacyImportMarker, "Validated backups imported from " + legacy.BackupDirectory
            + ". Original files were retained. " + DateTimeOffset.UtcNow.ToString("O"));
        return imported;
    }
}