using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using RTXLens.Contracts.Security;
using RTXLens.Installer.Actions.Validation;
using RTXLens.Presentation.Msi.Afterburner;

namespace RTXLens.Installer.Actions.Configuration;

internal sealed class LegacySafeguardMigration(string previous, string destination, string programData)
{
    private static readonly Dictionary<string, string> Scripts = new(StringComparer.Ordinal)
    {
        ["low-power-shutdown"] = "LowPowerAndShutdown.ps1",
        ["low-power-shutdown-notify"] = "LowPowerShutdownAndNotify.ps1",
        ["simulation"] = "SimulateSafeguard.ps1"
    };

    // Hashes from the public v1.0.6 package. MSI removes its scripts before installing the replacement.
    private static readonly Dictionary<string, string> ReleasedHashes = new(StringComparer.Ordinal)
    {
        ["LowPowerAndShutdown.ps1"] = "8D4C37557F9AFECFD4E48EAC3B3E19E1D089326846AAD11BD9349102C01C03B8",
        ["LowPowerShutdownAndNotify.ps1"] = "01296739FC2AB57F1858B66FAEAE769388C70DDAADBBE5D29453DC532CD34887",
        ["SafeguardWorkflow.ps1"] = "7310E4502AEBABA0A036DE03C152B32C6065C1B187EE11AD983E94C7076A2532",
        ["SimulateSafeguard.ps1"] = "4D687FDC31AFBAD017217500CCEEDB1740AA8BDBFC8801A17F96483678AA6A5F"
    };

    private static readonly Regex BackupName = new(@"^\d{8}-\d{6}-\d{3}-[0-9a-f]{32}$", RegexOptions.IgnoreCase);
    private static readonly JavaScriptSerializer Json = new();
    private static readonly string PowerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
    private const string Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File ";
    private const string Hidden = "-WindowStyle Hidden ";
    private const string Workflow = "SafeguardWorkflow.ps1";
    private string NewBindings => Path.Combine(programData, "RTXLens.Safeguard", "bindings");

    public void CloseAfterburnerBeforeMigration(Action<string> log)
    {
        if (!AfterburnerShutdown.IsRunning())
        {
            return;
        }


        var (candidates, profiles) = DiscoverBindings();
        foreach (var profile in profiles)
        {
            _ = RewriteProfile(ReadBytes(profile), candidates, strict: true, out var changed);
            if (changed)
            {
                AfterburnerShutdown.Close(AfterburnerExecutable(profile), log);
            }
        }
    }

    public void Capture(string journal)
    {
        var (candidates, profiles) = DiscoverBindings();
        foreach (var profile in profiles)
        {
            _ = RewriteProfile(ReadBytes(profile), candidates, strict: true, out var changed);
            if (changed)
            {
                AfterburnerShutdown.RequireStopped(AfterburnerExecutable(profile));
            }
        }

        File.WriteAllText(Path.Combine(journal, "bindings.json"), Json.Serialize(candidates));
        File.WriteAllText(Path.Combine(journal, "profiles.json"), Json.Serialize(profiles.ToArray()));
    }

    private (Dictionary<string, string> Candidates, HashSet<string> Profiles) DiscoverBindings()
    {
        var candidates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var profiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var store = Path.Combine(programData, "Rtx5090Monitoring.Safeguard");
        var bindings = Path.Combine(store, "bindings");
        LegacyInstallationMigration.AssertNoLinks(bindings);
        if (!Directory.Exists(bindings))
        {
            return (candidates, profiles);
        }

        RequireProtectedRecord(store);
        RequireProtectedRecord(bindings);

        foreach (var path in Directory.EnumerateFiles(bindings, "*.json"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id))
            {
                continue;
            }

            RequireProtectedRecord(path);
            var binding = ReadJson(path);
            if (!Guid.TryParse(Value(binding, "id"), out var storedId) || storedId != id
                || !Scripts.TryGetValue(Value(binding, "scriptId"), out var script))
            {
                continue;
            }

            var executable = Value(binding, "afterburnerExecutable");
            if (Path.IsPathRooted(executable) && Path.GetFileName(executable).Equals("MSIAfterburner.exe", StringComparison.OrdinalIgnoreCase))
            {
                var profile = Path.Combine(Path.GetDirectoryName(executable)!, "Profiles", "MSIAfterburner.cfg");
                if (File.Exists(profile))
                {
                    ProtectedInstallationFile.Require(executable);
                    ProtectedInstallationFile.Require(profile);
                    profiles.Add(profile);
                }
            }

            if (ReleasedHashes[script] == Value(binding, "scriptHash")
                && ReleasedHashes[Workflow] == Value(binding, "workflowHash"))
            {
                candidates.Add(id.ToString("N"), script);
            }
        }

        return (candidates, profiles);
    }

    public void Apply(string journal)
    {
        var plan = Path.Combine(journal, "bindings.json");
        if (!File.Exists(plan))
        {
            return;
        }

        var candidates = Json.Deserialize<Dictionary<string, string>>(File.ReadAllText(plan));
        var profiles = Json.Deserialize<string[]>(File.ReadAllText(Path.Combine(journal, "profiles.json")));
        if (candidates.Count == 0)
        {
            return;
        }

        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var script in candidates.Values.Append(Workflow).Distinct())
        {
            var path = ScriptPath(destination, script);
            ProtectedInstallationFile.Require(path);
            hashes.Add(script, Hash(File.ReadAllBytes(path)));
        }

        foreach (var candidate in candidates)
        {
            var path = Path.Combine(NewBindings, candidate.Key + ".json");
            RequireProtectedRecord(path);
            var before = ReadBytes(path);
            var binding = ParseJson(before);
            binding["scriptHash"] = hashes[candidate.Value];
            binding["workflowHash"] = hashes[Workflow];
            Replace(journal, path, before, EncodeJson(binding));
        }

        foreach (var profile in profiles)
        {
            ProtectedInstallationFile.Require(profile);
            var before = ReadBytes(profile);
            var after = RewriteProfile(before, candidates, strict: true, out var changed);
            if (changed)
            {
                AfterburnerShutdown.RequireStopped(AfterburnerExecutable(profile));
                Replace(journal, profile, before, after);
            }
        }

        UpdateBackups(journal, Path.Combine(destination, "Desktop", "AfterburnerBackups"), candidates);
        var oldBackups = Path.Combine(programData, "RTXLens.AfterburnerBackups");
        LegacyInstallationMigration.AssertNoLinks(oldBackups);
        if (Directory.Exists(oldBackups))
        {
            foreach (var folder in Directory.EnumerateDirectories(oldBackups).Where(path => Regex.IsMatch(Path.GetFileName(path), @"^[0-9a-fA-F]{16}$")))
            {
                UpdateBackups(journal, folder, candidates);
            }
        }
    }

    public void Restore(string journal)
    {
        foreach (var entry in Directory.EnumerateFiles(journal, "edit-*.json").OrderByDescending(path => path, StringComparer.Ordinal))
        {
            var data = Json.Deserialize<string[]>(File.ReadAllText(entry));
            var path = data[0];
            LegacyInstallationMigration.AssertNoLinks(path);
            var before = File.ReadAllBytes(Path.ChangeExtension(entry, ".bin"));
            var hash = Hash(ReadBytes(path));
            if (hash == data[1])
            {
                AtomicReplace(path, before);
            }
            else if (hash != Hash(before))
            {
                throw new IOException("An upgrade file was changed externally. The previous bytes are available in " + journal);
            }
        }
    }

    private byte[] RewriteProfile(byte[] before, Dictionary<string, string> candidates, bool strict, out bool changed)
    {
        var ini = new IniDocument(before);
        changed = false;
        for (var connector = 1; connector <= 2; connector++)
        {
            var section = "Source PSU 12VHPWR" + connector + " alarm";
            if (!string.Equals(ini.Get(section, "AlarmApp"), PowerShell, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = ini.Get(section, "AlarmAppCmdLine");
            if (value is null)
            {
                continue;
            }

            var prefix = value.StartsWith(Hidden, StringComparison.OrdinalIgnoreCase) ? Hidden + Arguments : Arguments;
            foreach (var script in Scripts.Values)
            {
                var start = prefix + '"' + ScriptPath(previous, script) + "\" -BindingId ";
                if (!value.StartsWith(start, StringComparison.OrdinalIgnoreCase)
                    || !Guid.TryParseExact(value.Substring(start.Length), "D", out var id))
                {
                    continue;
                }

                if (!candidates.TryGetValue(id.ToString("N"), out var verified) || script != verified)
                {
                    if (strict)
                    {
                        throw new IOException("An active Afterburner alarm refers to a missing or modified RTX Monitor script binding. Reapply the alarm in RTX Monitor before upgrading.");
                    }

                    break;
                }

                var replacement = Hidden + Arguments + '"' + ScriptPath(destination, script) + "\" -BindingId " + id.ToString("D");
                if (!string.Equals(value, replacement, StringComparison.Ordinal))
                {
                    ini.Set(section, "AlarmAppCmdLine", replacement);
                    changed = true;
                }

                break;
            }
        }

        return changed ? ini.ToBytes() : before;
    }

    private void UpdateBackups(string journal, string root, Dictionary<string, string> candidates)
    {
        LegacyInstallationMigration.AssertNoLinks(root);
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(root).Where(path => BackupName.IsMatch(Path.GetFileName(path))))
        {
            var profile = Path.Combine(directory, "MSIAfterburner.cfg");
            var manifest = Path.Combine(directory, "manifest.json");
            if (!File.Exists(profile) || !File.Exists(manifest))
            {
                continue;
            }

            var before = ReadBytes(profile);
            var metadata = ReadBytes(manifest);
            var data = ParseJson(metadata);
            if (data.TryGetValue("restorable", out var restorable) && restorable is false)
            {
                continue;
            }

            if (!data.TryGetValue("hashes", out var values) || values is not Dictionary<string, object> hashes
                || !hashes.TryGetValue("Profiles/MSIAfterburner.cfg", out var digest)
                || !string.Equals(digest as string, Hash(before), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var after = RewriteProfile(before, candidates, strict: false, out var changed);
            if (changed)
            {
                hashes["Profiles/MSIAfterburner.cfg"] = Hash(after);
                Replace(journal, profile, before, after);
                Replace(journal, manifest, metadata, EncodeJson(data));
            }
        }
    }

    private static void Replace(string journal, string path, byte[] before, byte[] after)
    {
        if (!ReadBytes(path).SequenceEqual(before))
        {
            throw new IOException("An upgrade file changed while it was being migrated: " + path);
        }

        var index = Directory.EnumerateFiles(journal, "edit-*.json").Count();
        var entry = Path.Combine(journal, "edit-" + index.ToString("D8"));
        File.WriteAllBytes(entry + ".bin", before);
        File.WriteAllText(entry + ".json", Json.Serialize(new[] { path, Hash(after) }));
        AtomicReplace(path, after);
    }

    private static void AtomicReplace(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
            }

            File.Replace(temporary, path, null);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static string AfterburnerExecutable(string profile) =>
        Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(profile)!)!, "MSIAfterburner.exe");

    private static void RequireProtectedRecord(string path)
    {
        LegacyInstallationMigration.AssertNoLinks(path);
        var security = Directory.Exists(path) ? (FileSystemSecurity)Directory.GetAccessControl(path) : File.GetAccessControl(path);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        const FileSystemRights writes = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.ChangePermissions
                                        | FileSystemRights.TakeOwnership | FileSystemRights.DeleteSubdirectoriesAndFiles;

        if (!Trusted(security.GetOwner(typeof(SecurityIdentifier)))
            || security.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
                .Any(rule => rule.AccessControlType == AccessControlType.Allow && !Trusted(rule.IdentityReference)
                    && (rule.FileSystemRights & writes) != 0))
        {
            throw new UnauthorizedAccessException("An unprotected Safeguard+ record cannot be migrated: " + path);
        }

        return;

        bool Trusted(IdentityReference? identity) => Equals(identity, administrators) || Equals(identity, system);
    }

    private static string ScriptPath(string installation, string script) => Path.Combine(installation, "Service", "SafeguardScripts", script);

    private static byte[] ReadBytes(string path)
    {
        LegacyInstallationMigration.AssertNoLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 4 * 1024 * 1024)
        {
            throw new InvalidDataException("An upgrade file exceeds the supported size: " + path);
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static Dictionary<string, object> ReadJson(string path) => ParseJson(ReadBytes(path));
    private static Dictionary<string, object> ParseJson(byte[] bytes) =>
        Json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
    private static byte[] EncodeJson(Dictionary<string, object> values) => Encoding.UTF8.GetBytes(Json.Serialize(values));

    private static string Value(Dictionary<string, object> values, string key) =>
        values.TryGetValue(key, out var value) ? value as string ?? "" : "";

    private static string Hash(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
    }
}
