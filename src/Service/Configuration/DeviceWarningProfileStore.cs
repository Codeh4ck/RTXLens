using RTXLens.Core.Plugins;
using System.Text.Json;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Core.Diagnostics;

namespace RTXLens.Service.Configuration;

internal sealed class DeviceWarningProfileStore(HardwareCatalog hardware, SettingsProfileStore settings, ILogger<DeviceWarningProfileStore> logger)
{
    private const int MaximumProfiles = 48;
    private static readonly AppJsonContext InputJson = new(new JsonSerializerOptions(AppJsonContext.Default.Options)
    {
        PropertyNameCaseInsensitive = true
    });

    private readonly Lock _gate = new();
    private DeviceWarningProfile[]? _profiles;
    private WarningProfileTarget[] _targets = [];

    private string PathOnDisk => Path.Combine(settings.DirectoryPath, "device-warnings.json");

    public DeviceWarningProfileCatalog Catalog(SettingsProfile? automatic, ServiceConfiguration configuration)
    {
        lock (_gate)
        {
            var defaults = automatic?.GpuModel == "5080" ? SettingsProfile.Rtx5080 : SettingsProfile.Rtx5090;
            return new([.. Load()], [.. _targets], ConnectorWarningLimits.From(defaults),
                ConnectorWarningLimits.From(automatic ?? defaults), defaults.GpuModel, configuration.Warnings.Defaults) { Sources = hardware.PinSources() };
        }
    }

    public void Evaluate(SensorSnapshot snapshot, Action<DeviceWarningProfile[]> evaluate)
    {
        lock (_gate)
        {
            _targets =
            [
                .. snapshot.Sensors.Select(sensor => sensor.Identity)
                    .Where(identity => hardware.Find(identity.PluginId)?.PinProfiles?.Component == identity.Component)
                    .Select(identity => new WarningProfileTarget(identity.PluginId, identity.DeviceId, identity.DeviceId))
                    .Distinct().OrderBy(target => target.DeviceId, StringComparer.Ordinal)
            ];

            evaluate(Load());
        }
    }

    public void Save(DeviceWarningProfile profile)
    {
        if (!ValidTarget(profile))
        {
            throw new ArgumentException("Check the source, thresholds, notification policy, and shutdown delay.");
        }

        lock (_gate)
        {
            var profiles = Load().ToList();
            var index = profiles.FindIndex(existing => existing.Id == profile.Id);

            if (index >= 0)
            {
                profiles[index] = profile;
            }
            else if (profiles.Count < MaximumProfiles)
            {
                profiles.Add(profile);
            }
            else
            {
                throw new ArgumentException($"Up to {MaximumProfiles} warning profiles can be saved.");
            }

            Write([.. profiles]);

            logger.LogActivity(LogLevel.Information, "protection.profile-saved", "Configuration", "Device warning profile saved.", profile.Name);
        }
    }

    public void Delete(string id)
    {
        lock (_gate)
        {
            var profiles = Load();
            var removed = profiles.FirstOrDefault(profile => profile.Id == id);

            if (removed is null)
            {
                throw new KeyNotFoundException("Warning profile not found.");
            }

            Write([.. profiles.Where(profile => profile.Id != id)]);

            logger.LogActivity(LogLevel.Information, "protection.profile-deleted", "Configuration", "Device warning profile deleted.", removed.Name);
        }
    }

    private DeviceWarningProfile[] Load()
    {
        if (_profiles is not null)
        {
            return _profiles;
        }

        try
        {
            if (!File.Exists(PathOnDisk))
            {
                return _profiles = [.. hardware.DefaultProfiles];
            }

            using var document = JsonDocument.Parse(File.ReadAllText(PathOnDisk));
            var saved = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().Select(ReadProfile).ToArray() : null;

            if (saved is { Length: <= MaximumProfiles } && saved.All(profile => profile.IsValid() && (hardware.Find(profile.PluginId) is null || ValidTarget(profile))) &&
                saved.Select(profile => profile.Id).Distinct(StringComparer.Ordinal).Count() == saved.Length)
            {
                return _profiles = saved;
            }

            logger.LogActivity(LogLevel.Warning, "protection.profiles-invalid", "Configuration",
                "The service ignored invalid device warning profiles.", eventViewer: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogActivity(LogLevel.Error, "protection.profiles-read-failed", "Configuration",
                "Could not load device warning profiles.", eventViewer: true, exception: error);
        }

        return _profiles = [];
    }

    private bool ValidTarget(DeviceWarningProfile profile) =>
        profile.IsValid() && hardware.Find(profile.PluginId)?.PinProfiles is { } support && support.AcceptsConnector(profile.ConnectorId)
        && (Guid.TryParseExact(profile.Id, "N", out _) || hardware.DefaultProfiles.Any(defaultProfile => defaultProfile.Id == profile.Id));

    public DeviceWarningProfile ReadProfile(JsonElement element)
    {
        var profile = element.Deserialize(InputJson.DeviceWarningProfile) ?? throw new JsonException("Empty warning profile.");
        var original = hardware.LegacyProfileDefaults;
        if (original is null)
        {
            return profile;
        }

        bool Has(string name) => element.EnumerateObject().Any(property =>
            string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase));

        return profile with
        {
            PluginId = Has("pluginId") ? profile.PluginId : original.PluginId,
            Name = Has("name") ? profile.Name : original.Name,
            Id = Has("id") ? profile.Id : original.Id
        };
    }

    private void Write(DeviceWarningProfile[] profiles)
    {
        Directory.CreateDirectory(settings.DirectoryPath);
        SettingsProfileStore.WriteAtomically(PathOnDisk, JsonSerializer.Serialize(profiles, AppJsonContext.Default.DeviceWarningProfileArray));
        _profiles = profiles;
    }
}