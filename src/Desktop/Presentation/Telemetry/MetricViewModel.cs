using RTXLens.Presentation.Settings;
using RTXLens.Presentation.Infrastructure;

namespace RTXLens.Presentation.Telemetry;

public sealed class MetricViewModel(string label, string value = "Unavailable", string? iconName = null) : ObservableObject
{
    public string Label { get; set => Set(ref field, value); } = label;
    public string Value
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                Raise(nameof(Tone));
            }
        }
    } = value;
    public bool IsWarning
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                Raise(nameof(Tone));
            }
        }
    }
    public StatusTone Tone => IsWarning ? StatusTone.Negative : Value == "Unavailable" || Value.Contains('—') ? StatusTone.Caution
        : Label is "Fault Status" or "Fault Log Status" ? Value == "OK" ? StatusTone.Positive : StatusTone.Negative
        : StatusTone.Neutral;
    public string IconName => iconName ?? Label switch
    {
        "Core clock" or "GPU usage" => "vcore",
        "Memory clock" or "VRAM used" or "VRAM free" or "VRAM total" => "vram",
        "Fan 1" or "Fan 2" or "Fan 1 / Fan 2" or "PSU fan" or "Fan mode" or "Requested fan duty" => DesktopIcons.Fan,
        "Core / VRAM temp" or "Board Temp In/Out" or "External Temp 1/2" => DesktopIcons.Temperature,
        "VRAM usage" => "vramusage",
        "Fault Status" or "Fault Log Status" => "overcurrent",
        "Cable rating" or "GPU board power" => "voltage",
        _ when Label.Contains("temperature", StringComparison.OrdinalIgnoreCase) || Label.StartsWith("Onboard", StringComparison.Ordinal)
            || Label.StartsWith("External probe", StringComparison.Ordinal) => DesktopIcons.Temperature,
        _ => "pin"
    };
}
