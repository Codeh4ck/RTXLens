using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;

namespace RTXLens.Presentation.Performance;

public sealed record PerformanceTrace(string Key, string Group, string Label, string Source, string DeviceId,
    string Unit, string Color, SensorIdentity Identity)
{
    public string Category => Identity.Metric is SensorMetric.Temperature or SensorMetric.FanSpeed
        ? "Temperatures"
        : Group.StartsWith("GPU ", StringComparison.Ordinal) || Group == "VRAM usage" ? "GPU metrics" : Group;

    public double ConvertValue(double value) => Unit switch
    {
        "mV" => value * 1000,
        "GB" => value / 1024,
        _ => value
    };

    public static PerformanceTrace? Create(SensorReading reading, string source, int colorIndex)
    {
        var identity = reading.Identity;
        var group = (identity.Component, identity.Metric) switch
        {
            (SensorComponent.GpuPin or SensorComponent.PsuPin, SensorMetric.Current) => "Pin current",
            (SensorComponent.GpuConnector or SensorComponent.PsuConnector, SensorMetric.Current) => "Total current",
            (SensorComponent.GpuConnector or SensorComponent.PsuConnector, SensorMetric.Power) => "Connector power",
            (SensorComponent.GpuConnector or SensorComponent.PsuConnector, SensorMetric.Voltage) => "Connector voltage",
            (SensorComponent.GpuCore, SensorMetric.Voltage) => "GPU core voltage",
            (SensorComponent.GpuCore, SensorMetric.Clock) => "GPU core clock",
            (SensorComponent.GpuMemory, SensorMetric.Clock) => "GPU memory clock",
            (SensorComponent.GpuMemory, SensorMetric.MemoryUsed) => "VRAM usage",
            (SensorComponent.GpuFan, SensorMetric.FanSpeed) => "GPU fan speed",
            (SensorComponent.GpuCore or SensorComponent.GpuMemory or SensorComponent.GpuHotspot, SensorMetric.Temperature) => "GPU temperatures",
            (SensorComponent.MonitorBoard or SensorComponent.ExternalProbe, SensorMetric.Temperature) => "Device temperatures",
            _ => null
        };

        if (group is null)
        {
            return null;
        }

        var unit = group switch
        {
            "Pin current" or "Total current" => "A",
            "Connector power" => "W",
            "GPU core voltage" => "mV",
            "Connector voltage" => "V",
            "GPU core clock" or "GPU memory clock" => "MHz",
            "VRAM usage" => "GB",
            "GPU fan speed" => "RPM",
            _ => "°C"
        };
        if (reading.Unit != SensorUnits.For(identity.Metric))
        {
            return null;
        }

        var connector = identity.ConnectorId is { } id ? $" · Connector {id}"
            : identity is { Component: SensorComponent.GpuConnector, Channel: > 1 } ? $" · Connector {identity.Channel}" : "";
        var metric = group == "Pin current" ? $"P{identity.Channel}"
            : group == "GPU fan speed" ? identity.Channel is { } fan ? $"Fan {fan}" : "Fan"
            : group == "VRAM usage" ? "VRAM used"
            : identity.Metric == SensorMetric.Temperature ? identity.Component switch
            {
                SensorComponent.GpuMemory => "VRAM",
                SensorComponent.GpuHotspot => "Hotspot",
                SensorComponent.MonitorBoard => identity.Channel switch
                {
                    1 => "Board temp in",
                    2 => "Board temp out",
                    _ => "Board temp"
                },
                SensorComponent.ExternalProbe => identity.Channel is { } probe ? $"External temp {probe}" : "External temp",
                _ => "Core"
            }
            : group;

        return new(identity.Key, group, $"{source}{connector} · {metric}", source, identity.DeviceId, unit, ColorFor(colorIndex), identity);
    }

    private static string ColorFor(int index)
    {
        var hue = (170 + index * 137.507764) % 360 / 60;
        const double chroma = 0.65;
        const double minimum = 0.25;
        var secondary = chroma * (1 - Math.Abs(hue % 2 - 1));
        var (red, green, blue) = (int)hue switch
        {
            0 => (chroma, secondary, 0d),
            1 => (secondary, chroma, 0d),
            2 => (0d, chroma, secondary),
            3 => (0d, secondary, chroma),
            4 => (secondary, 0d, chroma),
            _ => (chroma, 0d, secondary)
        };

        return $"#{(byte)Math.Round((red + minimum) * 255):X2}{(byte)Math.Round((green + minimum) * 255):X2}{(byte)Math.Round((blue + minimum) * 255):X2}";
    }
}
