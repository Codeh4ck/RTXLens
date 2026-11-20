using RTXLens.Contracts.Configuration;

namespace RTXLens.Service.Services;

internal sealed class HardwarePresenceTracker
{
    private int _absences;

    public PluginSettings? Observe(PluginSettings settings, bool? present)
    {
        if (present is null || settings is { Enabled: false, AutomaticallyDisabled: false })
        {
            _absences = 0;
            return null;
        }

        _absences = present.Value ? 0 : _absences + 1;
        return (present.Value && settings.AutomaticallyDisabled) || (!present.Value && settings.Enabled && _absences >= 2)
            ? settings with { Enabled = present.Value, AutomaticallyDisabled = !present.Value } : null;
    }
}
