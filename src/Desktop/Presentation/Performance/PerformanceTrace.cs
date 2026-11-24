using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Plugins.GenericNvidia.Contracts;
using RTXLens.Plugins.GenericNvidia.Contracts.Devices;
using RTXLens.Presentation.Telemetry;

namespace RTXLens.Presentation.Performance;

public sealed record PerformanceTrace(string Key, string Group, string Label, string Source, string DeviceId,
    string Unit, string Color, SensorIdentity Identity)
{
    public string Category => Identity.Component switch
    {
        SensorComponent.GpuFan => "Fans",
        SensorComponent.GpuPcie => "PCIe",
        SensorComponent.GpuPerformance or SensorComponent.GpuPowerLimits => "GPU power status",
        _ when Identity.Metric == SensorMetric.Temperature => "Temperatures",
        _ when Group.StartsWith("GPU ", StringComparison.Ordinal) || Group == "VRAM usage" => "GPU metrics",
        _ => Group
    };

    public string PlotGroup => Identity.Metric == SensorMetric.Clock ? "GPU clocks" : Group;

    public bool Stepped => Identity.Metric is SensorMetric.PerformanceState or SensorMetric.ActiveClockReasons
        or SensorMetric.LinkGeneration or SensorMetric.LinkWidth or SensorMetric.ReplayCount or SensorMetric.ReplayRolloverCount;

    public string FormatValue(double value) => Identity.Metric switch
    {
        SensorMetric.PerformanceState => $"P{value:0}",
        SensorMetric.LinkGeneration => GpuDiagnosticsViewModel.FormatLink(value, null),
        SensorMetric.LinkWidth => $"×{value:0}",
        SensorMetric.ActiveClockReasons => value == 1 ? "Active" : "Inactive",
        _ => $"{value:0.##} {Unit}".TrimEnd()
    };

    public double ConvertValue(double value) => Unit switch
    {
        "mV" => value * 1000,
        "MiB/s" => value / 1_048_576,
        "GT/s per lane" => value / 1000,
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
            (SensorComponent.GpuCrossbar, SensorMetric.Clock) => "GPU crossbar clock",
            (SensorComponent.GpuSystem, SensorMetric.Clock) => "GPU system clock",
            (SensorComponent.GpuCore, SensorMetric.Utilization) => "GPU utilization",
            (SensorComponent.GpuMemory, SensorMetric.Utilization) => "GPU memory usage percentage",
            (SensorComponent.GpuBoard, SensorMetric.Power) => "GPU board power",
            (SensorComponent.GpuFan, SensorMetric.FanDuty) => "GPU fan duty",
            (SensorComponent.GpuPowerLimits, SensorMetric.DefaultPowerLimit or SensorMetric.EnforcedPowerLimit
                or SensorMetric.MinimumPowerLimit or SensorMetric.MaximumPowerLimit) => "GPU power limits",
            (SensorComponent.GpuPerformance, SensorMetric.PerformanceState) => "Current P-state",
            (SensorComponent.GpuPerformance, SensorMetric.ActiveClockReasons) when KnownReason(identity.Channel) => "Active boost limits",
            (SensorComponent.GpuPerformance, SensorMetric.ClockLimitDuration) when KnownReason(identity.Channel) => "Boost limit duration",
            (SensorComponent.GpuPcie, SensorMetric.ReceiveThroughput or SensorMetric.TransmitThroughput) => "PCIe traffic",
            (SensorComponent.GpuPcie, SensorMetric.LinkGeneration) => "PCIe link generation",
            (SensorComponent.GpuPcie, SensorMetric.LinkWidth) => "PCIe link width",
            (SensorComponent.GpuPcie, SensorMetric.LinkSpeed) => "PCIe link speed",
            (SensorComponent.GpuPcie, SensorMetric.ReplayCount or SensorMetric.ReplayRolloverCount) => "PCIe replays",
            (SensorComponent.GpuFan, SensorMetric.FanSpeed) => "GPU fan speed",
            (SensorComponent.GpuCore or SensorComponent.GpuMemory or SensorComponent.GpuHotspot, SensorMetric.Temperature) => "GPU temperatures",
            (SensorComponent.MonitorBoard or SensorComponent.ExternalProbe, SensorMetric.Temperature) => "Device temperatures",
            _ => null
        };

        if (group is null)
        {
            return null;
        }

        if (reading.Unit != SensorUnits.For(identity.Metric))
        {
            return null;
        }

        var unit = reading.Unit switch
        {
            SensorUnit.Amperes => "A",
            SensorUnit.Watts => "W",
            SensorUnit.Volts => identity.Component == SensorComponent.GpuCore ? "mV" : "V",
            SensorUnit.Megahertz => "MHz",
            SensorUnit.Megabytes => "GB",
            SensorUnit.Rpm => "RPM",
            SensorUnit.Percent => "%",
            SensorUnit.BytesPerSecond => "MiB/s",
            SensorUnit.MegabitsPerSecond => "GT/s per lane",
            SensorUnit.Seconds => "s",
            SensorUnit.Celsius => "°C",
            _ => ""
        };

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
            : identity.Metric switch
            {
                SensorMetric.FanDuty => $"Fan {identity.Channel} duty",
                SensorMetric.ActiveClockReasons or SensorMetric.ClockLimitDuration =>
                    GpuDiagnosticsViewModel.ReasonLabel((NvidiaClockReason)identity.Channel!.Value),
                SensorMetric.DefaultPowerLimit => "Default power limit",
                SensorMetric.EnforcedPowerLimit => "Enforced power limit",
                SensorMetric.MinimumPowerLimit => "Minimum power limit",
                SensorMetric.MaximumPowerLimit => "Maximum power limit",
                SensorMetric.ReceiveThroughput => "PCIe RX",
                SensorMetric.TransmitThroughput => "PCIe TX",
                SensorMetric.ReplayRolloverCount => "PCIe replay rollovers",
                _ => group
            };

        if (identity is { PluginId: NvidiaIdentity.Id, Component: SensorComponent.GpuConnector } && source.StartsWith("ROG Astral", StringComparison.Ordinal))
        {
            metric += " (NVIDIA)";
        }

        return new(identity.Key, group, $"{source}{connector} · {metric}", source, identity.DeviceId, unit, ColorFor(colorIndex), identity);
    }

    private static bool KnownReason(int? channel) => channel is > 0 && Enum.IsDefined((NvidiaClockReason)channel.Value);

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
