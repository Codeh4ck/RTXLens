using System.Text.Json;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Core.Diagnostics;

namespace RTXLens.Service.Configuration;

internal sealed class DeviceWarningProfileStore(SettingsProfileStore settings, ILogger<DeviceWarningProfileStore> logger)
{
    private const int MaximumProfiles = 48;

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
                ConnectorWarningLimits.From(automatic ?? defaults), defaults.GpuModel, configuration.Warnings.Defaults);
        }
    }

    public void Evaluate(SensorSnapshot snapshot, Action<DeviceWarningProfile[]> evaluate)
    {
        lock (_gate)
        {
            _targets =
            [
                .. snapshot.Sensors.Select(sensor => sensor.Identity)
                    .Where(identity => identity is { PluginId: "msi-psu", Component: SensorComponent.PsuPin }
                        or { PluginId: "asus-astral" or "wireview-pro2", Component: SensorComponent.GpuPin })
                    .Select(identity => new WarningProfileTarget(identity.PluginId, identity.DeviceId, identity.DeviceId))
                    .Distinct().OrderBy(target => target.DeviceId, StringComparer.Ordinal)
            ];

            evaluate(Load());
        }
    }

    public void Save(DeviceWarningProfile profile)
    {
        if (!profile.IsValid())
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
                return _profiles = [new DeviceWarningProfile()];
            }

            var saved = JsonSerializer.Deserialize(File.ReadAllText(PathOnDisk), AppJsonContext.Default.DeviceWarningProfileArray);

            if (saved is { Length: <= MaximumProfiles } && saved.All(profile => profile?.IsValid() == true) &&
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

    private void Write(DeviceWarningProfile[] profiles)
    {
        Directory.CreateDirectory(settings.DirectoryPath);
        SettingsProfileStore.WriteAtomically(PathOnDisk, JsonSerializer.Serialize(profiles, AppJsonContext.Default.DeviceWarningProfileArray));
        _profiles = profiles;
    }
}