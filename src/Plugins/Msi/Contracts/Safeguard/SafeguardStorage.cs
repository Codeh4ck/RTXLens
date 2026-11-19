using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace RTXLens.Plugins.Msi.Contracts.Safeguard;

[SupportedOSPlatform("windows")]
public sealed class SafeguardStorage : ISafeguardJournalStorage
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemAccount = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RTXLens.Safeguard");

    private readonly string _root;
    private readonly HashSet<string> _verifiedAreas = [];
    private readonly Lock _gate = new();

    public SafeguardStorage(string? root = null)
    {
        _root = Path.GetFullPath(root ?? DefaultRoot);
        ProtectDirectory(_root);
    }

    public string Location(string area, Guid id) => Path.Combine(Area(area), id.ToString("N") + ".json");

    public T? Read<T>(string area, Guid id) where T : class => ReadFile<T>(Location(area, id));

    private static T? ReadFile<T>(string path) where T : class
    {
        EnsureNoLinks(path);

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

            return stream.Length > 128 * 1024
                ? throw new InvalidDataException("Safeguard+ record is too large.")
                : JsonSerializer.Deserialize<T>(stream, Json);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    public void Write<T>(string area, Guid id, T value) => WriteFile(Location(area, id), value);

    private static void WriteFile<T>(string path, T value)
    {
        EnsureNoLinks(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            var security = new FileSecurity();
            security.SetOwner(Administrators);
            security.SetAccessRuleProtection(true, false);

            foreach (var account in new[] { Administrators, SystemAccount })
            {
                security.AddAccessRule(new FileSystemAccessRule(account, FileSystemRights.FullControl, AccessControlType.Allow));
            }

            using (var stream = new FileInfo(temporary).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, security))
            {
                JsonSerializer.Serialize(stream, value, Json);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public void Delete(string area, Guid id)
    {
        var path = Location(area, id);
        EnsureNoLinks(path);

        File.Delete(path);
    }

    public IEnumerable<Guid> Ids(string area) => Directory.EnumerateFiles(Area(area), "*.json")
        .Select(path => Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id) ? id : Guid.Empty)
        .Where(id => id != Guid.Empty);

    public int RetentionDays
    {
        get => Math.Clamp(ReadFile<RetentionSetting>(Path.Combine(_root, "retention.json"))?.Days
            ?? SafeguardScripts.DefaultRetentionDays, 1, 365);
        set
        {
            if (value is < 1 or > 365)
            {
                throw new ArgumentException("History retention must be between 1 and 365 days.");
            }

            WriteFile(Path.Combine(_root, "retention.json"), new RetentionSetting(value));
        }
    }

    public static bool IsRunning(SafeguardExecution execution, DateTimeOffset now) =>
        execution.FinishedAt is null && now - execution.StartedAt <= TimeSpan.FromMilliseconds(execution.DelayMs + 30_000)
        && execution.StartedAt >= now - TimeSpan.FromMilliseconds(Environment.TickCount64) - TimeSpan.FromSeconds(2);

    public static string DisplayStatus(SafeguardExecution execution, DateTimeOffset now) =>
        execution.FinishedAt is not null || IsRunning(execution, now) ? execution.Status
        : execution.Status == "Submitting shutdown" ? "Shutdown request unconfirmed" : "Interrupted";

    public void DeleteExecutions(IEnumerable<Guid> ids, DateTimeOffset now)
    {
        var selection = ids.Distinct().ToArray();

        if (selection.Any(id => Read<SafeguardExecution>("history", id) is { } execution && IsRunning(execution, now)))
        {
            throw new InvalidOperationException("An active execution cannot be deleted.");
        }

        foreach (var id in selection)
        {
            DeleteExecutionHistory(id);
        }
    }

    public void DeleteExecutionHistory(Guid id)
    {
        Delete("history", id);
        if (!File.Exists(Location("outbox", id)))
        {
            Delete("telemetry", id);
            Delete("delivery", id);
        }
    }

    private string Area(string area)
    {
        if (area is not ("bindings" or "mail" or "requests" or "results" or "history" or "outbox" or "delivery" or "seen" or "telemetry"))
        {
            throw new ArgumentOutOfRangeException(nameof(area));
        }

        var path = Path.Combine(_root, area);

        lock (_gate)
        {
            if (_verifiedAreas.Contains(area))
            {
                return path;
            }

            ProtectDirectory(path);
            _verifiedAreas.Add(area);
        }

        return path;
    }

    private static void EnsureNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("Safeguard+ does not support linked files or folders.");
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void ProtectDirectory(string path)
    {
        EnsureNoLinks(path);
        var directory = new DirectoryInfo(path);

        if (!directory.Exists)
        {
            var security = new DirectorySecurity();
            security.SetOwner(Administrators);
            security.SetAccessRuleProtection(true, false);

            foreach (var account in new[] { Administrators, SystemAccount })
            {
                security.AddAccessRule(new FileSystemAccessRule(account, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            }

            directory.Create(security);
        }

        EnsureNoLinks(path);

        var actual = directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);

        if (!Trusted(actual.GetOwner(typeof(SecurityIdentifier))) || actual.GetAccessRules(true, true,
                    typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>().Any(rule => rule.AccessControlType == AccessControlType.Allow && !Trusted(rule.IdentityReference)))
        {
            throw new UnauthorizedAccessException("The Safeguard+ data folder has unexpected permissions.");
        }
    }

    private static bool Trusted(IdentityReference? identity) => Equals(identity, Administrators) || Equals(identity, SystemAccount);

    private sealed record RetentionSetting(int Days);
}