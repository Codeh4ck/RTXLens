using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Contracts.Warnings;
using RTXLens.Core.Diagnostics;

namespace RTXLens.Service.Mobile;

internal sealed class MobileWarningHistory(string directory, ILogger<MobileWarningHistory> logger)
{
    private const int MaxRecords = 2_000;
    private const long MaxDiskBytes = 64L * 1024 * 1024;
    private const int MaxReadings = 512;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, string> _active = [];
    private Dictionary<Guid, StoredIncident>? _records;
    private long _bytes;
    private DateTimeOffset _nextCleanup;
    private DateTimeOffset _nextWrite;
    private int _retention;

    public void Observe(SensorSnapshot snapshot, int retentionDays)
    {
        if (snapshot.Warnings is not { } warnings)
        {
            return;
        }

        lock (_gate)
        {
            var now = snapshot.Timestamp;
            if (now < _nextWrite)
            {
                return;
            }

            try
            {
                var records = Load();
                var current = new HashSet<Guid>();
                foreach (var warning in warnings.States)
                {
                    if (!Guid.TryParseExact(warning.EpisodeId, "N", out var id))
                    {
                        continue;
                    }

                    current.Add(id);
                    if (warning.Phase == WarningPhase.Active && !records.ContainsKey(id))
                    {
                        var readings = snapshot.Sensors.Where(reading =>
                            reading is { Quality: ReadingQuality.Valid, Value: not null }
                            && reading.Identity.PluginId == warning.PluginId && reading.Identity.DeviceId == warning.DeviceId)
                            .Take(MaxReadings).ToArray();
                        var incident = new MobileIncident(id, "warning", warning.Title, warning.PluginId, warning.StartedAt, null, "Active")
                        {
                            DeviceId = warning.DeviceId, Detail = warning.Detail, CapturedAt = snapshot.Timestamp, Readings = readings
                        };
                        var stored = Save(incident);
                        records.Add(id, stored);
                        _bytes += stored.Size;
                    }

                    if (records.ContainsKey(id))
                    {
                        _active[id] = warning.Phase.ToString();
                    }
                }

                foreach (var (id, phase) in _active.Where(entry => !current.Contains(entry.Key)).ToArray())
                {
                    var previous = records[id];
                    var incident = (Read(Location(id)) ?? previous.Summary) with
                    {
                        EndedAt = now, Status = phase == nameof(WarningPhase.Recovering) ? "Cleared" : "Ended"
                    };
                    var stored = Save(incident);
                    records[id] = stored;
                    _bytes += stored.Size - previous.Size;
                    _active.Remove(id);
                }

                if (_retention != retentionDays || _nextCleanup <= now || records.Count > MaxRecords || _bytes > MaxDiskBytes)
                {
                    Clean(records, now, retentionDays);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                _nextWrite = now.AddSeconds(30);
                logger.LogActivity(LogLevel.Warning, "mobile.history-write-failed", "Mobile access",
                    "Could not save mobile warning history.", error.GetType().Name, exception: error);
            }
        }
    }

    public MobileIncident[] Summaries(DateTimeOffset cutoff)
    {
        lock (_gate)
        {
            return [.. (_records?.Values.AsEnumerable() ?? []).Select(record => record.Summary)
                .Where(record => record.StartedAt >= cutoff || _active.ContainsKey(record.Id))];
        }
    }

    public MobileIncident? Find(Guid id, DateTimeOffset cutoff)
    {
        lock (_gate)
        {
            var record = _records?.GetValueOrDefault(id)?.Summary;
            if (record is null || (record.StartedAt < cutoff && !_active.ContainsKey(id)))
            {
                return null;
            }

            try
            {
                return Read(Location(id)) is { } detail && detail.Id == id ? detail with { Status = record.Status } : null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }
    }

    private Dictionary<Guid, StoredIncident> Load()
    {
        if (_records is { } records)
        {
            return records;
        }

        if (OperatingSystem.IsWindows())
        {
            ProtectDirectory();
        }
        else
        {
            Directory.CreateDirectory(directory);
        }

        records = [];
        long bytes = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id))
            {
                continue;
            }

            try
            {
                if (Read(path) is { } record && record.Id == id)
                {
                    var recovered = record.EndedAt is null ? record with { Status = "Interrupted" } : record;
                    var stored = Summarize(recovered, new FileInfo(path).Length);
                    records.Add(id, stored);
                    bytes += stored.Size;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                logger.LogActivity(LogLevel.Warning, "mobile.history-record-invalid", "Mobile access",
                    "Could not read a mobile warning record.", error.GetType().Name);
            }
        }

        _bytes = bytes;
        return _records = records;
    }

    [SupportedOSPlatform("windows")]
    private void ProtectDirectory()
    {
        var folder = new DirectoryInfo(directory);
        if (folder.Exists && folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("The mobile warning history folder cannot be a link.");
        }

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        using var identity = WindowsIdentity.GetCurrent();
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        foreach (var account in new[] { administrators, system, identity.User! }.Distinct())
        {
            security.AddAccessRule(new FileSystemAccessRule(account, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }

        if (folder.Exists)
        {
            folder.SetAccessControl(security);
        }
        else
        {
            folder.Create(security);
        }
    }

    private void Clean(Dictionary<Guid, StoredIncident> records, DateTimeOffset now, int days)
    {
        var cutoff = now.AddDays(-days);
        var removable = records.Values.Where(record => !_active.ContainsKey(record.Summary.Id))
            .OrderBy(record => record.Summary.StartedAt).ToArray();
        foreach (var record in removable)
        {
            if (record.Summary.StartedAt >= cutoff && records.Count <= MaxRecords && _bytes <= MaxDiskBytes)
            {
                break;
            }
            File.Delete(Location(record.Summary.Id));
            records.Remove(record.Summary.Id);
            _bytes -= record.Size;
        }

        _retention = days;
        _nextCleanup = now.AddMinutes(15);
    }

    private string Location(Guid id) => Path.Combine(directory, id.ToString("N") + ".json");

    private static MobileIncident? Read(string path)
    {
        if (!File.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            return null;
        }

        using var stream = File.OpenRead(path);
        return stream.Length <= MobileHistoryEndpoints.MaximumReportBytes
            ? JsonSerializer.Deserialize<MobileIncident>(stream, MobileHistoryEndpoints.Json) : null;
    }

    private StoredIncident Save(MobileIncident record)
    {
        var path = Location(record.Id);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, MobileHistoryEndpoints.Json);
        if (bytes.Length > MobileHistoryEndpoints.MaximumReportBytes)
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(record with { Readings = [] }, MobileHistoryEndpoints.Json);
        }

        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
            return Summarize(record, bytes.Length);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static StoredIncident Summarize(MobileIncident record, long size) =>
        new(record with { Readings = [], Detail = null }, size);

    private sealed record StoredIncident(MobileIncident Summary, long Size);
}
