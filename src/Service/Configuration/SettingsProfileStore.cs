using System.Text.Json;
using RTXLens.Contracts.Configuration;

namespace RTXLens.Service.Configuration;

internal sealed class SettingsProfileStore
{
    private readonly string _directory;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SettingsProfile> _profiles = new(StringComparer.Ordinal)
    {
        ["rtx-5090"] = SettingsProfile.Rtx5090,
        ["rtx-5080"] = SettingsProfile.Rtx5080
    };

    private string _selectedId = "auto";
    private SettingsProfile? _session;

    public SettingsProfileStore(IConfiguration configuration, ILogger<SettingsProfileStore> logger, string? defaultDirectory = null)
    {
        var executable = new DirectoryInfo(AppContext.BaseDirectory);
        var desktop = executable.Name.Equals("Service", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(executable.Parent!.FullName, "Desktop")
            : executable.FullName;
        _directory = Path.GetFullPath(configuration["SettingsProfiles:Directory"] ?? defaultDirectory ?? Path.Combine(desktop, "config"));

        try
        {
            if (!Directory.Exists(_directory))
            {
                return;
            }

            foreach (var path in Directory.EnumerateFiles(_directory, "*.profile.json"))
            {
                try
                {
                    var profile = JsonSerializer.Deserialize(File.ReadAllText(path), AppJsonContext.Default.SettingsProfile);
                    if (profile is not null && profile.IsValid() && Guid.TryParseExact(profile.Id, "N", out _)
                        && Path.GetFileName(path) == profile.Id + ".profile.json")
                    {
                        _profiles[profile.Id] = profile;
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
                {
                    logger.LogWarning(error, "Could not load GPU settings profile {File}", Path.GetFileName(path));
                }
            }

            var selectedPath = Path.Combine(_directory, "active-profile.txt");
            if (File.Exists(selectedPath) && File.ReadAllText(selectedPath).Trim() is { } id && (id == "auto" || _profiles.ContainsKey(id)))
            {
                _selectedId = id;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(error, "Could not read GPU settings profiles");
        }
    }

    public (string Id, SettingsProfile? Profile) Selection()
    {
        lock (_gate)
        {
            return (_selectedId, _session ?? _profiles.GetValueOrDefault(_selectedId));
        }
    }

    public SettingsProfileCatalog Catalog(SettingsProfile? effective)
    {
        lock (_gate)
        {
            return new(_selectedId, _session ?? _profiles.GetValueOrDefault(_selectedId) ?? effective, [.. _profiles.Values], effective);
        }
    }

    public void Apply(SettingsProfileRequest request)
    {
        lock (_gate)
        {
            if (request.ProfileId != "auto" && !_profiles.ContainsKey(request.ProfileId))
            {
                throw new ArgumentException("Select an available GPU settings profile.");
            }

            if (request.Settings is { } settings && !settings.IsValid())
            {
                throw new ArgumentException("Check the profile name, chart limits, warning thresholds and notification settings.");
            }

            if (!request.Save)
            {
                if (request.Settings is null && (request.ProfileId != "auto" || Directory.Exists(_directory)))
                {
                    Directory.CreateDirectory(_directory);
                    WriteAtomically(Path.Combine(_directory, "active-profile.txt"), request.ProfileId);
                }

                _selectedId = request.ProfileId;
                _session = request.Settings;
                return;
            }

            var saved = (request.Settings ?? throw new ArgumentException("Enter the settings to save.")) with
            {
                Id = Guid.NewGuid().ToString("N")
            };
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, saved.Id + ".profile.json");
            WriteAtomically(path, JsonSerializer.Serialize(saved, AppJsonContext.Default.SettingsProfile));

            try
            {
                WriteAtomically(Path.Combine(_directory, "active-profile.txt"), saved.Id);
            }
            catch
            {
                File.Delete(path);
                throw;
            }

            _profiles.Add(saved.Id, saved);
            _selectedId = saved.Id;
            _session = null;
        }
    }

    internal string DirectoryPath => _directory;

    internal static void WriteAtomically(string path, string content)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(System.Text.Encoding.UTF8.GetBytes(content));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
