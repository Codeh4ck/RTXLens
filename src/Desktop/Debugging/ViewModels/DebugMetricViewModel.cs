using System.Globalization;
using RTXLens.Contracts.Enums;
using RTXLens.Debugging.Models;
using RTXLens.Presentation.Infrastructure;

namespace RTXLens.Debugging.ViewModels;

public sealed class DebugMetricViewModel(DebugMetricDefinition definition) : ObservableObject
{
    public const string NotReported = "Not reported";

    private DebugMetricMode _mode;
    private double _value = definition.DefaultValue;
    public DebugMetricDefinition Definition { get; } = definition;

    public event Action? Edited;

    public DebugMetricMode Mode => _mode;

    public int ModeIndex
    {
        get => (int)_mode;
        set
        {
            if (value is < 0 or > 3 || !Set(ref _mode, (DebugMetricMode)value))
            {
                return;
            }

            Raise(nameof(Mode));
            Raise(nameof(IsValueEditable));
            Edited?.Invoke();
        }
    }

    public bool IsValueEditable => Mode is DebugMetricMode.Fixed or DebugMetricMode.Stale;
    public double Value
    {
        get => _value;
        set
        {
            if (!double.IsFinite(value) || value < Definition.Minimum || value > Definition.Maximum || (Definition.IsInteger && Math.Abs(value - Math.Truncate(value)) > 0.0001))
            {
                Error = $"Use {(Definition.IsInteger ? "an integer" : "a number")} from {Definition.Minimum} to {Definition.Maximum:0}.";
                return;
            }

            Error = "";

            if (Set(ref _value, value))
            {
                Raise(nameof(BooleanValue));
                Edited?.Invoke();
            }
        }
    }
    public string LiveText { get; private set => Set(ref field, value); } = NotReported;
    public string EffectiveText { get; private set => Set(ref field, value); } = "Effective: not reported";
    public string EffectiveValueText { get; private set => Set(ref field, value); } = NotReported;
    public string? EffectiveQuality { get; private set => Set(ref field, value); }
    public string Error { get; private set => Set(ref field, value); } = "";

    public bool BooleanValue { get => Math.Abs(Value - 1) < 0.0001; set => Value = value ? 1 : 0; }
    public void UpdateEffective(double? value, string quality)
    {
        EffectiveValueText = value is { } reading ? FormatReading(reading) : NotReported;
        EffectiveQuality = quality is nameof(ReadingQuality.Valid) or NotReported ? null : quality;
        EffectiveText = $"Effective: {EffectiveValueText} · {quality} · Live: {LiveText}";
    }

    public void UpdateLive(double? value) => LiveText = value is { } reading ? FormatReading(reading) : NotReported;

    private string FormatReading(double value)
    {
        if (Definition.IsBoolean)
        {
            return value == 0 ? "Off" : "On";
        }

        var unit = Definition.Unit switch
        {
            SensorUnit.Watts => " W",
            SensorUnit.Amperes => " A",
            SensorUnit.Volts => " V",
            SensorUnit.Celsius => " °C",
            SensorUnit.Megahertz => " MHz",
            SensorUnit.Rpm => " RPM",
            SensorUnit.Percent => "%",
            SensorUnit.Megabytes => " MB",
            SensorUnit.BytesPerSecond => " B/s",
            SensorUnit.MegabitsPerSecond => " Mb/s",
            SensorUnit.Seconds => " s",
            _ => ""
        };

        return value.ToString("0.###", CultureInfo.CurrentCulture) + unit;
    }

    public void Reset()
    {
        _value = Definition.DefaultValue;
        _mode = DebugMetricMode.Live;
        Error = "";
        Raise(string.Empty);
        Edited?.Invoke();
    }
}
