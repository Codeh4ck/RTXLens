using System.Collections.ObjectModel;
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

    public ObservableCollection<MetricViewModel> Metrics { get; } =
    [
        new("Core clock", iconName: "vcore"),
        new("Memory clock", iconName: "vram"),
        new("VRAM usage", iconName: "vramusage"),
        new("Core / VRAM temp", iconName: "vtemp"),
        new("Fan 1 / 2", iconName: "vfan")
    ];

    private readonly MetricViewModel _coreTemperature = new("Core temp", iconName: "vtemp");
    private readonly MetricViewModel _memoryTemperature = new("VRAM temp", iconName: "vtemp");
    private readonly MetricViewModel _hotspotTemperature = new("Hotspot temp", iconName: "vtemp");
    private readonly MetricViewModel _fan1 = new("Fan 1", iconName: "vfan");
    private readonly MetricViewModel _fan2 = new("Fan 2", iconName: "vfan");

    public ObservableCollection<MetricViewModel> DetailedMetrics { get; } = [];
    public GpuDiagnosticsViewModel Diagnostics { get; } = new();

    public GpuStatusViewModel()
    {
        foreach (var metric in Metrics.Take(3).Concat([_coreTemperature, _memoryTemperature, _fan1]))
        {
            DetailedMetrics.Add(metric);
        }
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
        Metrics[0].Value = Format(Value(SensorComponent.GpuCore, SensorMetric.Clock), "MHz", 0);
        Metrics[1].Value = Format(Value(SensorComponent.GpuMemory, SensorMetric.Clock), "MHz", 0);

        var used = Value(SensorComponent.GpuMemory, SensorMetric.MemoryUsed);
        var total = Value(SensorComponent.GpuMemory, SensorMetric.MemoryTotal);
        var percentage = Value(SensorComponent.GpuMemory, SensorMetric.Utilization);
        Metrics[2].Value = used is { } usedMb && total is > 0 and var totalMb
            ? $"{Format(usedMb / 1024, "GB", 2)} / {Format(totalMb / 1024, "GB", 2)} ({Format(percentage ?? usedMb / totalMb * 100, "%", 0)})"
            : "Unavailable";

        var temperatures = new List<string>
        {
            Temperature(Value(SensorComponent.GpuCore, SensorMetric.Temperature)),
            Temperature(Value(SensorComponent.GpuMemory, SensorMetric.Temperature))
        };
        var hotspot = Value(SensorComponent.GpuHotspot, SensorMetric.Temperature);
        Metrics[3].Label = hotspot is null ? "Core / VRAM temp" : "Core / VRAM / HS";

        if (hotspot is not null)
        {
            temperatures.Add(Temperature(hotspot));
        }

        Metrics[3].Value = string.Join(" / ", temperatures);
        Metrics[3].IsWarning = snapshot.Warnings?.States.Any(warning => warning.PluginId == PluginId
                                                                        && warning.DeviceId == devices[0] && warning is { Phase: Contracts.Warnings.WarningPhase.Active, Icon: "temperature" }) == true;

        var fans = readings.Where(reading => reading.Identity is { Component: SensorComponent.GpuFan, Metric: SensorMetric.FanSpeed, Channel: > 0 })
            .OrderBy(reading => reading.Identity.Channel).Take(2).ToArray();
        Metrics[4].Label = fans.Length == 0 ? "Fans" : "Fan " + string.Join(" / ", Enumerable.Range(1, fans.Length));
        Metrics[4].Value = fans.Length == 0 ? "Unavailable" : string.Join(" / ", fans.Select(fan =>
        {
            var rpm = Format(Value(SensorComponent.GpuFan, SensorMetric.FanSpeed, fan.Identity.Channel), "RPM", 0);
            var duty = Value(SensorComponent.GpuFan, SensorMetric.FanDuty, fan.Identity.Channel);
            return duty is null ? rpm : $"{rpm} ({Format(duty, "%", 0)})";
        }));

        _coreTemperature.Value = Temperature(Value(SensorComponent.GpuCore, SensorMetric.Temperature));
        _memoryTemperature.Value = Temperature(Value(SensorComponent.GpuMemory, SensorMetric.Temperature));
        _hotspotTemperature.Value = Temperature(hotspot);
        _coreTemperature.IsWarning = snapshot.Warnings?.States.Any(warning => warning.PluginId == PluginId
                                                                              && warning.DeviceId == devices[0] && warning is { Phase: Contracts.Warnings.WarningPhase.Active, TypeId: "generic-nvidia/core-temperature" }) == true;
        _memoryTemperature.IsWarning = snapshot.Warnings?.States.Any(warning => warning.PluginId == PluginId
                                                                                && warning.DeviceId == devices[0] && warning is { Phase: Contracts.Warnings.WarningPhase.Active, TypeId: "generic-nvidia/memory-temperature" }) == true;
        _fan1.Label = fans.Length == 0 ? "Fans" : "Fan 1";
        _fan1.Value = Fan(fans.ElementAtOrDefault(0));
        _fan2.Value = Fan(fans.ElementAtOrDefault(1));

        var detailed = Metrics.Take(3).Concat([_coreTemperature, _memoryTemperature]).ToList();
        if (hotspot is not null)
        {
            detailed.Add(_hotspotTemperature);
        }

        detailed.Add(_fan1);
        if (fans.Length > 1)
        {
            detailed.Add(_fan2);
        }

        foreach (var metric in DetailedMetrics.Except(detailed).ToArray())
        {
            DetailedMetrics.Remove(metric);
        }

        for (var index = 0; index < detailed.Count; index++)
        {
            var existing = DetailedMetrics.IndexOf(detailed[index]);
            if (existing < 0)
            {
                DetailedMetrics.Insert(index, detailed[index]);
            }
            else if (existing != index)
            {
                DetailedMetrics.Move(existing, index);
            }
        }

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
        Diagnostics.Clear();

        foreach (var metric in Metrics.Concat(DetailedMetrics).Distinct())
        {
            metric.Value = "Unavailable";
            metric.IsWarning = false;
        }
    }

    private static string Format(double? value, string unit, int decimals) => value is { } number && double.IsFinite(number)
        ? number.ToString("F" + decimals, CultureInfo.CurrentCulture) + (unit == "%" ? "" : " ") + unit : "Unavailable";
}
