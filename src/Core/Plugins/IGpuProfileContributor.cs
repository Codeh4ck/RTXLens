using RTXLens.Contracts.Configuration;

namespace RTXLens.Core.Plugins;

public interface IGpuProfileContributor
{
    string? SelectedDevice(PluginSettings settings) => null;
    SettingsProfile ApplyProfileDefaults(SettingsProfile profile, PluginSettings settings);
}
