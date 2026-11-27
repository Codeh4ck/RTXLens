using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Discovery;

namespace RTXLens.Core.Plugins;

public sealed class HardwareCatalog
{
    private readonly Dictionary<string, PluginHardwareMetadata> _entries = new(StringComparer.Ordinal);
    private readonly List<(string Id, IGpuProfileContributor Contributor)> _contributors = [];

    public HardwareCatalog(IEnumerable<ISensorPluginFactory> factories)
    {
        foreach (var factory in factories)
        {
            _entries.Add(factory.Id, factory.Hardware);
            if (factory is IGpuProfileContributor contributor)
            {
                _contributors.Add((factory.Id, contributor));
            }
        }
    }

    public PluginHardwareMetadata? Find(string id) => _entries.GetValueOrDefault(id);

    public Dictionary<string, PluginHardwareMetadata> PinSources() => _entries.Where(pair => pair.Value.PinProfiles is not null).ToDictionary(StringComparer.Ordinal);

    public IEnumerable<DeviceWarningProfile> DefaultProfiles =>
        _entries.Values.Select(entry => entry.PinProfiles?.DefaultProfile).OfType<DeviceWarningProfile>();

    public DeviceWarningProfile? LegacyProfileDefaults => _entries.Values
        .FirstOrDefault(entry => entry.PinProfiles?.SuppliesLegacyProfileDefaults == true)?.PinProfiles?.DefaultProfile;

    public string? SelectedDevice(ServiceConfiguration config)
    {
        foreach (var (id, contributor) in _contributors)
        {
            if (config.Plugins.TryGetValue(id, out var settings) && contributor.SelectedDevice(settings) is { } device)
            {
                return device;
            }
        }

        return null;
    }

    public SettingsProfile ApplyDefaults(SettingsProfile profile, ServiceConfiguration config)
    {
        foreach (var (id, contributor) in _contributors)
        {
            if (config.Plugins.TryGetValue(id, out var settings))
            {
                profile = contributor.ApplyProfileDefaults(profile, settings);
            }
        }

        return profile;
    }

    public bool UsePluginWarning(string pluginId, string typeId)
    {
        var support = Find(pluginId)?.PinProfiles;
        return support?.ReplacePluginWarnings != true && support?.OvercurrentAlert != typeId;
    }
}
