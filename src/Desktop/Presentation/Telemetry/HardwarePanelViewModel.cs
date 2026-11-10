using System.Collections.ObjectModel;
using System.Globalization;
using RTXLens.Contracts.Discovery;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Plugins.WireView.Contracts.Common;
using RTXLens.Presentation.Infrastructure;
using RTXLens.Presentation.Settings;

namespace RTXLens.Presentation.Telemetry;

public sealed class HardwarePanelViewModel : ObservableObject
{
    private string? _deviceId;
    private DateTimeOffset? _lastSuccess;
    private int _staleAfterMs = 5000;
    private double? _pinLimit;
    private int _psuConnector = 1;
    private string[] _genericKeys = [];
    private PluginCapability[] _capabilities;

    public string Id { get; }
    public string Name { get; }
    public bool IsWireView => Id == "wireview-pro2";
    public bool IsAsus => Id == "asus-astral";
    public bool IsNvidia => Id == "generic-nvidia";
    public bool HasGpuStatus => IsAsus || IsWireView || IsNvidia;
    public bool IsMsi => Id == "msi-psu";
    public MsiPanelViewModel Msi { get; } = new();

    public bool Enabled
    {
        get;
        set
        {
            if (Set(ref field, value) && !value)
            {
                IsReporting = false;
            }
        }
    }

    public bool IsReporting
    {
        get;
        private set
        {
            if (Set(ref field, value))
            {
                Raise(nameof(ShowClearFaults));
            }
        }
    }

    public bool CanReadDevice => _capabilities.Contains(PluginCapability.DeviceConfigurationRead);
    public bool CanClearFaults => _capabilities.Contains(PluginCapability.FaultClear);
    public bool ShowClearFaults => CanClearFaults && IsReporting && HasFaults;
    public bool CanRestart => _capabilities.Contains(PluginCapability.Restart);

    public bool HasPins
    {
        get;
        private set => Set(ref field, value);
    }

    public string State
    {
        get;
        private set
        {
            if (Set(ref field, value))
            {
                Raise(nameof(Tone));
            }
        }
    }

    public StatusTone Tone => State switch
    {
        "Ready" => StatusTone.Positive,
        "Starting" or "Stopping" or "Stale" or "NoDevice" or "Disabled" => StatusTone.Caution,
        "Error" or "Disconnected" or "Unavailable" => StatusTone.Negative,
        _ => StatusTone.Neutral
    };

    public bool IsSelected
    {
        get;
        set => Set(ref field, value);
    }

    public bool ShowPeakHold
    {
        get;
        private set => Set(ref field, value);
    }

    public bool ShowHeadroom
    {
        get;
        private set => Set(ref field, value);
    }

    public bool ShowPsuComparison
    {
        get;
        private set => Set(ref field, value);
    }

    public string PsuComparisonLabel => $"MSI PSU · CONNECTOR {_psuConnector} · A";

    public string Message
    {
        get;
        private set => Set(ref field, value);
    }

    public string PowerText
    {
        get;
        private set => Set(ref field, value);
    } = "—";

    public string CurrentText
    {
        get;
        private set => Set(ref field, value);
    } = "—";

    public string VoltageText
    {
        get;
        private set => Set(ref field, value);
    } = "—";

    public string BoardPowerText
    {
        get;
        private set => Set(ref field, value);
    } = "—";

    public string GpuUsageText
    {
        get;
        private set => Set(ref field, value);
    } = "—";

    public string CurrentSpreadText
    {
        get;
        private set => Set(ref field, value);
    } = "—";

    public string HighestCurrentText
    {
        get;
        private set => Set(ref field, value);
    } = "—";
    public string LowestCurrentText
    {
        get;
        private set => Set(ref field, value);
    } = "—";
    public string WarningText
    {
        get;
        private set
        {
            if (Set(ref field, value))
            {
                Raise(nameof(HasWarning));
            }
        }
    } = string.Empty;
    public bool HasWarning => !string.IsNullOrEmpty(WarningText);
    public string DeviceText
    {
        get;
        set => Set(ref field, value);
    } = string.Empty;
    public DeviceConfig? DeviceConfiguration { get; private set; }
    public string DeviceConfigurationStatus { get; private set; } = "Read settings from the connected device.";
    public ObservableCollection<PinViewModel> Pins { get; } = [.. Enumerable.Range(1, 6).Select(channel => new PinViewModel(channel))];
    public ObservableCollection<MetricViewModel> Metrics { get; } = [];
    public MetricViewModel CableRating { get; } = new("Cable rating");
    public MetricViewModel BoardTemperatures { get; } = new("Board Temp In/Out");
    public MetricViewModel ExternalTemperatures { get; } = new("External Temp 1/2");
    public MetricViewModel FaultStatus { get; } = new("Fault Status");
    public MetricViewModel FaultLogStatus { get; } = new("Fault Log Status");
    public bool HasFaults => Faults.Count > 0;
    public ObservableCollection<MetricViewModel> Faults { get; } = [];
    public ObservableCollection<MetricViewModel> DeviceDetails { get; } = [];

    public HardwarePanelViewModel(PluginInfo plugin)
    {
        Id = plugin.Id;
        Name = Id switch { "wireview-pro2" => "WireView", "asus-astral" => "ROG Astral", "msi-psu" => "MSI PSU", "generic-nvidia" => "GPU status", _ => plugin.Name };
        _capabilities = plugin.Capabilities;
        Enabled = plugin.Enabled;
        State = plugin.State.ToString();
        Message = plugin.Message ?? State;
        if (IsWireView)
        {
            Metrics.Add(CableRating);
            Metrics.Add(BoardTemperatures);
            Metrics.Add(ExternalTemperatures);
            Metrics.Add(FaultStatus);
            Metrics.Add(FaultLogStatus);
        }
    }

    public void UpdateDiscovery(PluginInfo plugin)
    {
        Enabled = plugin.Enabled;
        if (_capabilities.SequenceEqual(plugin.Capabilities))
        {
            return;
        }

        _capabilities = plugin.Capabilities;
        Raise(nameof(CanReadDevice));
        Raise(nameof(CanClearFaults));
        Raise(nameof(ShowClearFaults));
        Raise(nameof(CanRestart));
    }

    public void Configure(int staleAfterMs, double? currentLimit)
    {
        _staleAfterMs = staleAfterMs;
        if (IsAsus || _pinLimit is null)
        {
            _pinLimit = currentLimit;
        }
    }

    public void Apply(SensorSnapshot snapshot, AppPreferences preferences, DateTimeOffset now, bool updateDetails = true)
    {
        ShowPeakHold = preferences.ShowPeakHold;
        ShowHeadroom = preferences.ShowHeadroom;
        var health = snapshot.Plugins.FirstOrDefault(plugin => plugin.PluginId == Id);
        _lastSuccess = health?.LastSuccessAt;
        State = health?.State.ToString() ?? "Unavailable";
        Message = health?.Message ?? (State == "Ready" ? "Receiving telemetry" : State);
        if (!Enabled || health?.State != PluginState.Ready || _lastSuccess is { } success && now - success > TimeSpan.FromMilliseconds(_staleAfterMs))
        {
            if (health?.State == PluginState.Ready)
            {
                State = "Stale";
            }

            ClearReadings(preferences);
            return;
        }

        var readings = snapshot.Sensors.Where(reading => reading.Identity.PluginId == Id).ToArray();
        IsReporting = readings.Any(reading => reading is { Quality: ReadingQuality.Valid, Value: { } value } && double.IsFinite(value)
            && now - reading.ObservedAt <= TimeSpan.FromMilliseconds(_staleAfterMs));
        if (!IsReporting)
        {
            State = "Unavailable";
            Message = "No current sensor readings received";
            ClearReadings(preferences);
            return;
        }
        var deviceId = readings.FirstOrDefault()?.Identity.DeviceId;
        if (deviceId != _deviceId)
        {
            foreach (var pin in Pins)
            {
                pin.ClearPeakHistory();
            }

            DeviceConfiguration = null;
            DeviceDetails.Clear();
            SetDeviceConfigurationStatus("Read settings from the connected device.");
            if (IsWireView)
            {
                _pinLimit = null;
            }

            _deviceId = deviceId;
            DeviceText = IsMsi ? "MSI MPG TS · PSU-side measurements" : deviceId ?? "Connected device";
        }

        if (IsMsi)
        {
            Msi.Update(readings, preferences, now, _staleAfterMs, updateDetails);
            return;
        }

        if (IsNvidia)
        {
            return;
        }

        var power = Value(SensorComponent.GpuConnector, SensorMetric.Power, 1);
        var current = Value(SensorComponent.GpuConnector, SensorMetric.Current, 1);

        if (updateDetails)
        {
            PowerText = Format(power, "W", 1);
            CurrentText = Format(current, "A", 2);
            VoltageText = Format(Value(SensorComponent.GpuConnector, SensorMetric.Voltage, 1), "V", 2);
        }

        var activeProfile = snapshot.SettingsProfile;
        var profile = activeProfile?.AppliesToConnector(Id, deviceId) == true ? activeProfile.Profile : null;
        var profileLimit = snapshot.PinWarningLimits is { } limits
            ? limits.FirstOrDefault(limit => limit.PluginId == Id && limit.DeviceId == deviceId)?.OvercurrentAmperes
            : IsAsus || profile?.UseWireViewOvercurrentFallback == true ? profile?.OvercurrentAmperes : null;
        var deviceLimit = IsWireView || snapshot.PinWarningLimits is null ? _pinLimit : null;
        var currentLimit = profileLimit is { } softwareLimit && deviceLimit is { } hardwareLimit
            ? Math.Min(softwareLimit, hardwareLimit) : profileLimit ?? deviceLimit;
        var cableRatingW = Value(SensorComponent.GpuConnector, SensorMetric.PowerCapability, 1);

        foreach (var pin in Pins)
        {
            pin.Update(Value(SensorComponent.GpuPin, SensorMetric.Current, pin.Channel), Value(SensorComponent.GpuPin, SensorMetric.Voltage, pin.Channel),
                Value(SensorComponent.GpuPin, SensorMetric.Power, pin.Channel), preferences.PinMetric, currentLimit, preferences.ShowPeakHold, preferences.ShowHeadroom, cableRatingW, profile, updateDetails);
        }

        HasPins = Pins.Any(pin => pin.Current is not null || pin.Voltage is not null || pin.Power is not null);

        if (!updateDetails)
        {
            return;
        }

        var currents = Pins.Where(pin => pin.Current is not null).ToArray();
        var highest = currents.MaxBy(pin => pin.Current);
        var lowest = currents.MinBy(pin => pin.Current);

        CurrentSpreadText = currents.Length >= 2 ? Format(highest!.Current - lowest!.Current, "A", 2) : "—";
        HighestCurrentText = highest is null ? "—" : $"P{highest.Channel} · {Format(highest.Current, "A", 2)}";
        LowestCurrentText = lowest is null ? "—" : $"P{lowest.Channel} · {Format(lowest.Current, "A", 2)}";

        var softwareWarnings = snapshot.Warnings?.States.Where(warning => warning.ProfileId is not null
            && warning.PluginId == Id && warning.DeviceId == deviceId && warning.Phase == Contracts.Warnings.WarningPhase.Active)
            .Select(warning => $"{warning.Title}: {warning.Detail}").ToArray() ?? [];

        if (IsWireView)
        {
            var statusValue = Value(SensorComponent.MonitorBoard, SensorMetric.FaultStatus);
            var logValue = Value(SensorComponent.MonitorBoard, SensorMetric.FaultLog);
            var status = (ushort)(statusValue ?? 0);
            var log = (ushort)(logValue ?? 0);

            CableRating.Value = Format(Value(SensorComponent.GpuConnector, SensorMetric.PowerCapability, 1), "W", 0);
            BoardTemperatures.Value = Pair(
                Temperature(Value(SensorComponent.MonitorBoard, SensorMetric.Temperature, 1), preferences),
                Temperature(Value(SensorComponent.MonitorBoard, SensorMetric.Temperature, 2), preferences));
            ExternalTemperatures.Value = Pair(
                Temperature(Value(SensorComponent.ExternalProbe, SensorMetric.Temperature, 1), preferences),
                Temperature(Value(SensorComponent.ExternalProbe, SensorMetric.Temperature, 2), preferences));

            FaultStatus.Value = statusValue is null ? "Unavailable" : status == 0 ? "OK" : "Fault detected";
            FaultLogStatus.Value = logValue is null ? "Unavailable" : log == 0 ? "OK" : "Faults logged";

            UpdateFaults(status, log);

            WarningText = string.Join(Environment.NewLine, status == 0 ? softwareWarnings : [FaultDescriptions.Describe(status), .. softwareWarnings]);
        }
        else if (IsAsus && snapshot.PinWarningLimits is not null)
        {
            WarningText = string.Join(Environment.NewLine, softwareWarnings);
        }
        else if (IsAsus)
        {
            var warnings = snapshot.Alerts.Where(alert => alert.PluginId == Id).Select(alert =>
                $"{alert.Kind}: {string.Join(", ", alert.Pins.Select(pin => $"P{pin.Channel} {pin.CurrentA:F2} A"))}"
                + (alert.PercentageDifference is { } percentage ? $" - {percentage:F0}% difference" : string.Empty)).ToList();

            if (Pins.Any(pin => pin.Voltage is > 0 and <= 11.5 or >= 12.5))
            {
                warnings.Add("Pin voltage outside 11.5-12.5 V");
            }

            WarningText = string.Join(Environment.NewLine, warnings);
        }
        else if (!IsNvidia)
        {
            var keys = readings.Select(reading => reading.Identity.Key).ToArray();
            if (!_genericKeys.SequenceEqual(keys))
            {
                _genericKeys = keys;
                Metrics.Clear();
                foreach (var reading in readings)
                {
                    Metrics.Add(new MetricViewModel($"{reading.Identity.Component} {reading.Identity.Channel} · {reading.Identity.Metric}"));
                }
            }
            for (var index = 0; index < readings.Length; index++)
            {
                Metrics[index].Value = readings[index].Quality == ReadingQuality.Valid
                    ? Format(readings[index].Value, readings[index].Unit.ToString(), 2) : readings[index].Quality.ToString();
            }
        }

        return;

        double? Value(SensorComponent component, SensorMetric metric, int? channel = null)
        {
            var reading = readings.FirstOrDefault(item => item.Identity.Component == component && item.Identity.Metric == metric
                                                                                               && (channel is null || item.Identity.Channel == channel));
            return reading is { Quality: ReadingQuality.Valid, Value: { } value } && double.IsFinite(value)
                                                                                  && now - reading.ObservedAt <= TimeSpan.FromMilliseconds(_staleAfterMs) ? value : null;
        }
    }

    public void MarkDisconnected(AppPreferences preferences)
    {
        State = "Disconnected";
        Message = "Waiting for service connection";
        ClearReadings(preferences);
    }

    public void UpdatePsuComparison(SensorReading[] readings, AppPreferences preferences)
    {
        var enabled = IsWireView ? preferences.ShowWireViewPsuCurrent : IsAsus && preferences.ShowAsusPsuCurrent;
        var connector = IsWireView ? preferences.WireViewPsuConnector : preferences.AsusPsuConnector;
        var matching = readings.Where(reading => reading.Identity.ConnectorId == connector.ToString(CultureInfo.InvariantCulture)).ToArray();

        ShowPsuComparison = enabled && Enabled && IsReporting && HasPins && matching.Length > 0;

        if (_psuConnector != connector)
        {
            _psuConnector = connector;
            Raise(nameof(PsuComparisonLabel));
        }

        foreach (var pin in Pins)
        {
            var values = matching.Where(reading => reading.Identity.Channel == pin.Channel).ToArray();
            pin.SetPsuCurrent(ShowPsuComparison, ShowPsuComparison && values.Length == 1 ? values[0].Value : null);
        }
    }

    public void MarkExpired(DateTimeOffset now, AppPreferences preferences)
    {
        if (State == "Ready" && _lastSuccess is { } success && now - success > TimeSpan.FromMilliseconds(_staleAfterMs))
        {
            State = "Stale";
            Message = "The last reading has expired";
            ClearReadings(preferences);
        }
    }

    public void SetDeviceConfiguration(DeviceConfig config, string? status = null)
    {
        DeviceConfiguration = config;
        _pinLimit = config.WireOcpFaultThresholdA;
        DeviceDetails.Clear();

        foreach (var metric in WireViewDeviceMetrics.Create(config))
        {
            DeviceDetails.Add(metric);
        }

        DeviceConfigurationStatus = status ?? $"Read from device · {DateTimeOffset.Now:HH:mm:ss}";

        Raise(nameof(DeviceConfigurationStatus));
    }

    public void SetDeviceConfigurationStatus(string status)
    {
        if (DeviceConfigurationStatus == status)
        {
            return;
        }

        DeviceConfigurationStatus = status;

        Raise(nameof(DeviceConfigurationStatus));
    }

    private void UpdateFaults(ushort status, ushort log)
    {
        var warnings = WireViewFaultMetrics.Create(status, log);
        if (Faults.Select(metric => (metric.Label, metric.Value)).SequenceEqual(warnings.Select(metric => (metric.Label, metric.Value))))
        {
            return;
        }

        Faults.Clear();

        foreach (var warning in warnings)
        {
            Faults.Add(warning);
        }

        Raise(nameof(HasFaults));
        Raise(nameof(ShowClearFaults));
    }

    private void ClearReadings(AppPreferences preferences)
    {
        if (IsMsi)
        {
            Msi.Update([], preferences, DateTimeOffset.UtcNow, _staleAfterMs);
        }

        IsReporting = false;

        if (IsWireView)
        {
            DeviceConfiguration = null;
            if (DeviceDetails.Count > 0)
            {
                DeviceDetails.Clear();
            }
            SetDeviceConfigurationStatus("Device unavailable. Refresh after reconnecting.");
        }

        ShowPsuComparison = false;
        PowerText = CurrentText = VoltageText = BoardPowerText = GpuUsageText = "—";
        CurrentSpreadText = HighestCurrentText = LowestCurrentText = "—";
        ShowPeakHold = preferences.ShowPeakHold;
        ShowHeadroom = preferences.ShowHeadroom;
        WarningText = string.Empty;

        UpdateFaults(0, 0);

        foreach (var metric in Metrics)
        {
            metric.Value = "Unavailable";
            metric.IsWarning = false;
        }

        foreach (var pin in Pins)
        {
            pin.SetPsuCurrent(false, null);
            pin.Update(null, null, null, preferences.PinMetric, _pinLimit, preferences.ShowPeakHold, preferences.ShowHeadroom);
        }
    }


    private static string Temperature(double? value, AppPreferences preferences) =>
        Format(preferences.Fahrenheit && value is not null ? (value * 9 / 5) + 32 : value, preferences.Fahrenheit ? "°F" : "°C", 1);

    private static string Pair(string first, string second) => first == "Unavailable" && second == "Unavailable" ? "Unavailable"
        : $"{(first == "Unavailable" ? "—" : first)} / {(second == "Unavailable" ? "—" : second)}";

    private static string MemoryUsage(double? usedMb, double? totalMb, double? percentage) => usedMb is null || totalMb is not > 0 ? "Unavailable"
        : $"{Format(usedMb / 1024, "GB", 2)} / {Format(totalMb / 1024, "GB", 2)} ({Format(percentage ?? usedMb / totalMb * 100, "%", 0).Replace(" %", "%", StringComparison.Ordinal)})";

    private static string Format(double? value, string unit, int decimals) => value is { } number
        ? $"{number.ToString(decimals == 0 ? "0" : "0." + new string('#', decimals), CultureInfo.CurrentCulture)} {unit}" : "Unavailable";
}
