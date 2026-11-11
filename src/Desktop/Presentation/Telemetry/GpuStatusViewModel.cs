using System.Globalization;
using System.Text.Json;
using RTXLens.Client.Serialization;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Monitoring;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Presentation.Infrastructure;
using RTXLens.Presentation.Settings;
using RTXLens.Plugins.GenericNvidia.Contracts.Configuration;

namespace RTXLens.Presentation.Telemetry;

public sealed class GpuStatusViewModel : ObservableObject
{
    public const string PluginId = "generic-nvidia";

    private DateTimeOffset? _lastObservedAt;
    private int _staleAfterMs = 5000;

    public string GpuUsageText { get; private set => Set(ref field, value); } = "-";
    public string CoreVoltageText { get; private set => Set(ref field, value); } = "-";
    public string PowerText { get; private set => Set(ref field, value); } = "Unavailable";
    public string CurrentText { get; private set => Set(ref field, value); } = "Unavailable";
    public string VoltageText { get; private set => Set(ref field, value); } = "Unavailable";
    public string DeviceText { get; private set => Set(ref field, value); } = "Selected GeForce RTX GPU";
    public string State { get; private set => Set(ref field, value); } = "Waiting for telemetry";

    public bool IsUnavailable
    {
        get;
        private set => Set(ref field, value);
    } = true;

    public MetricViewModel CoreClock { get; } = new("Core clock", iconName: "vcore");
    public MetricViewModel MemoryClock { get; } = new("Memory clock", iconName: "vram");
    public MetricViewModel VramUsage { get; } = new("VRAM usage", iconName: "vramusage");
    public MetricViewModel CoreTemperature { get; } = new("Core temp", iconName: "vtemp");
    public MetricViewModel MemoryTemperature { get; } = new("VRAM temp", iconName: "vtemp");
    public MetricViewModel HotspotTemperature { get; } = new("Hotspot temp", iconName: "vtemp");
    public MetricViewModel Fan1 { get; } = new("Fans", iconName: "vfan");
    public MetricViewModel Fan2 { get; } = new("Fan 2", iconName: "vfan");

    public double VramUsagePercent { get; private set => Set(ref field, value); }
    public bool HasVramUsage { get; private set => Set(ref field, value); }
    public bool HasHotspotTemperature { get; private set => Set(ref field, value); }
    public bool HasSecondFan { get; private set => Set(ref field, value); }
    public GpuDiagnosticsViewModel Diagnostics { get; } = new();

    private readonly MetricViewModel[] _metrics;

    public GpuStatusViewModel()
    {
        _metrics = [CoreClock, MemoryClock, VramUsage, CoreTemperature, MemoryTemperature, HotspotTemperature, Fan1, Fan2];
    }

    public void Apply(SensorSnapshot snapshot, ConfigurationSnapshot? configuration, AppPreferences preferences, DateTimeOffset now)
    {
        var settings = configuration?.Configuration.Plugins.GetValueOrDefault(PluginId);
        var options = settings?.Options?.Deserialize(ClientJsonContext.Default.GenericNvidiaOptions) ?? new GenericNvidiaOptions();
        _staleAfterMs = settings?.StaleAfterMs ?? 5000;
        var health = snapshot.Plugins.FirstOrDefault(plugin => plugin.PluginId == PluginId);
        var readings = snapshot.Sensors.Where(reading => reading.Identity.PluginId == PluginId).ToArray();
        var devices = readings.Select(reading => reading.Identity.DeviceId).Distinct(StringComparer.Ordinal).ToArray();

        if (settings?.Enabled == false || health?.State != PluginState.Ready || devices.Length != 1
            || !readings.Any(Valid))
        {
            Clear(health is null ? "GenericNvidia is unavailable. Update or configure the backend service."
                : health.State == PluginState.Ready ? "GPU readings have expired" : health.Message ?? health.State.ToString());
            return;
        }

        IsUnavailable = false;
        State = "Receiving GPU telemetry";
        DeviceText = health.Message ?? devices[0];
        _lastObservedAt = readings.Where(Valid).Max(reading => reading.ObservedAt);
        GpuUsageText = Format(Value(SensorComponent.GpuCore, SensorMetric.Utilization), "%", 0);
        CoreVoltageText = Format(Value(SensorComponent.GpuCore, SensorMetric.Voltage) * 1000, "mV", 1);
        Diagnostics.Apply(Value);

        var electrical = options.ElectricalSource switch
        {
            "None" => [],
            "WireView" when snapshot.Plugins.Any(plugin => plugin is { PluginId: "wireview-pro2", State: PluginState.Ready }) =>
                snapshot.Sensors.Where(reading => reading.Identity.PluginId == "wireview-pro2").ToArray(),
            "WireView" => [],
            _ => readings
        };
        var electricalAge = options.ElectricalSource == "WireView"
            ? configuration?.Configuration.Plugins.GetValueOrDefault("wireview-pro2")?.StaleAfterMs ?? 5000 : _staleAfterMs;

        double? Input(SensorMetric metric)
        {
            var selection = snapshot.Selections.FirstOrDefault(item => item.Name == ConnectorProfiles.SelectionName(metric));
            if (configuration?.Configuration.Monitoring.ConnectorProfiles.Version == 1 || selection is not null)
            {
                var selected = snapshot.Sensors.FirstOrDefault(reading => reading.Identity.Key == selection?.SensorKey);
                var age = configuration?.Configuration.Plugins.GetValueOrDefault(selected?.Identity.PluginId ?? "")?.StaleAfterMs ?? 5000;
                return selected is { Quality: ReadingQuality.Valid, Value: { } number } && double.IsFinite(number)
                    && snapshot.Plugins.Any(plugin => plugin.PluginId == selected.Identity.PluginId && plugin.State == PluginState.Ready)
                    && now - selected.ObservedAt <= TimeSpan.FromMilliseconds(age) ? number : null;
            }

            if (electrical.Select(reading => reading.Identity.DeviceId).Distinct().Take(2).Count() != 1)
            {
                return null;
            }

            var reading = electrical.FirstOrDefault(reading => reading.Identity is { Component: SensorComponent.GpuConnector, Channel: 1 }
                && reading.Identity.Metric == metric);
            return reading is { Quality: ReadingQuality.Valid, Value: { } value } && double.IsFinite(value)
                && now - reading.ObservedAt <= TimeSpan.FromMilliseconds(electricalAge) ? value : null;
        }

        PowerText = Format(Input(SensorMetric.Power), "W", 1);
        CurrentText = Format(Input(SensorMetric.Current), "A", 2);
        VoltageText = Format(Input(SensorMetric.Voltage), "V", 2);
        CoreClock.Value = Format(Value(SensorComponent.GpuCore, SensorMetric.Clock), "MHz", 0);
        MemoryClock.Value = Format(Value(SensorComponent.GpuMemory, SensorMetric.Clock), "MHz", 0);

        var used = Value(SensorComponent.GpuMemory, SensorMetric.MemoryUsed);
        var total = Value(SensorComponent.GpuMemory, SensorMetric.MemoryTotal);
        var reportedVramPercent = Value(SensorComponent.GpuMemory, SensorMetric.Utilization);
        var vramPercent = reportedVramPercent ?? (total is > 0 ? used / total * 100 : null);
        if (used is { } usedMb && total is > 0 and var totalMb)
        {
            HasVramUsage = vramPercent is { } percent && double.IsFinite(percent);
            VramUsagePercent = vramPercent is { } value && HasVramUsage ? Math.Clamp(value, 0, 100) : 0;
            VramUsage.Value = $"{Format(usedMb / 1024, "GB", 2)} / {Format(totalMb / 1024, "GB", 2)} ({Format(vramPercent, "%", 0)})";
        }
        else
        {
            HasVramUsage = false;
            VramUsagePercent = 0;
            VramUsage.Value = "Unavailable";
        }

        var hotspot = Value(SensorComponent.GpuHotspot, SensorMetric.Temperature);
        CoreTemperature.Value = Temperature(Value(SensorComponent.GpuCore, SensorMetric.Temperature));
        MemoryTemperature.Value = Temperature(Value(SensorComponent.GpuMemory, SensorMetric.Temperature));
        HotspotTemperature.Value = Temperature(hotspot);
        HasHotspotTemperature = hotspot.HasValue;

        CoreTemperature.IsWarning = snapshot.Warnings?.States.Any(warning => warning.PluginId == PluginId
            && warning.DeviceId == devices[0] && warning is { Phase: Contracts.Warnings.WarningPhase.Active, TypeId: "generic-nvidia/core-temperature" }) == true;
        MemoryTemperature.IsWarning = snapshot.Warnings?.States.Any(warning => warning.PluginId == PluginId
            && warning.DeviceId == devices[0] && warning is { Phase: Contracts.Warnings.WarningPhase.Active, TypeId: "generic-nvidia/memory-temperature" }) == true;

        var fans = readings.Where(reading => reading.Identity is { Component: SensorComponent.GpuFan, Metric: SensorMetric.FanSpeed, Channel: > 0 })
            .OrderBy(reading => reading.Identity.Channel).Take(2).ToArray();
        Fan1.Label = fans.Length == 0 ? "Fans" : "Fan 1";
        Fan1.Value = Fan(fans.ElementAtOrDefault(0));
        Fan2.Value = Fan(fans.ElementAtOrDefault(1));
        HasSecondFan = fans.Length > 1;

        return;

        string Fan(SensorReading? fan)
        {
            if (fan is null)
            {
                return "Unavailable";
            }

            var rpm = Format(Value(SensorComponent.GpuFan, SensorMetric.FanSpeed, fan.Identity.Channel), "RPM", 0);
            var duty = Value(SensorComponent.GpuFan, SensorMetric.FanDuty, fan.Identity.Channel);
            return duty is null ? rpm : $"{rpm} ({Format(duty, "%", 0)})";
        }

        bool Valid(SensorReading reading) => reading is { Quality: ReadingQuality.Valid, Value: { } value } && double.IsFinite(value)
            && now - reading.ObservedAt <= TimeSpan.FromMilliseconds(_staleAfterMs);

        double? Value(SensorComponent component, SensorMetric metric, int? channel = null)
        {
            var reading = readings.FirstOrDefault(reading => reading.Identity.Component == component && reading.Identity.Metric == metric
                && reading.Identity.Channel == channel);
            return reading is not null && Valid(reading) ? reading.Value : null;
        }

        string Temperature(double? value) => Format(preferences.Fahrenheit ? value * 9 / 5 + 32 : value,
            preferences.Fahrenheit ? "°F" : "°C", 1);
    }

    public void MarkExpired(DateTimeOffset now)
    {
        if (_lastObservedAt is { } observed && now - observed > TimeSpan.FromMilliseconds(_staleAfterMs))
        {
            Clear("GPU readings have expired");
        }
    }

    public void Clear(string message = "Waiting for service connection")
    {
        IsUnavailable = true;
        State = message;
        _lastObservedAt = null;
        PowerText = CurrentText = VoltageText = "Unavailable";
        GpuUsageText = CoreVoltageText = "-";
        HasVramUsage = HasHotspotTemperature = HasSecondFan = false;
        VramUsagePercent = 0;
        Fan1.Label = "Fans";
        Diagnostics.Clear();

        foreach (var metric in _metrics)
        {
            metric.Value = "Unavailable";
            metric.IsWarning = false;
        }
    }

    private static string Format(double? value, string unit, int decimals) => value is { } number && double.IsFinite(number)
        ? number.ToString("F" + decimals, CultureInfo.CurrentCulture) + (unit == "%" ? "" : " ") + unit : "Unavailable";
}
