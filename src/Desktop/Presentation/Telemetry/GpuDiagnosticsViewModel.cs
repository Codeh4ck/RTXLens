using System.Collections.ObjectModel;
using System.Globalization;
using RTXLens.Contracts.Enums;
using RTXLens.Plugins.GenericNvidia.Contracts.Devices;

namespace RTXLens.Presentation.Telemetry;

public sealed class GpuDiagnosticsViewModel
{
    private const string Unavailable = "Unavailable";
    private static readonly NvidiaClockReason[] ClockReasons = Enum.GetValues<NvidiaClockReason>()
        .Where(reason => reason != NvidiaClockReason.None).ToArray();
    private static readonly NvidiaClockReason[] TimedReasons =
    [
        NvidiaClockReason.SoftwarePowerCap,
        NvidiaClockReason.SoftwareThermalSlowdown,
        NvidiaClockReason.HardwareThermalSlowdown,
        NvidiaClockReason.HardwarePowerBrake,
        NvidiaClockReason.SyncBoost
    ];

    private readonly Dictionary<NvidiaClockReason, MetricViewModel> _boostLimitRows = ClockReasons.ToDictionary(
        reason => reason, reason => new MetricViewModel(ReasonLabel(reason), "-"));
    private readonly MetricViewModel _unknownBoostLimits = new("Unknown boost limits", "-");
    private readonly MetricViewModel _emptyBoostLimits = new(Unavailable, "-");
    private NvidiaClockReason _visibleBoostLimits;
    private readonly MetricViewModel _performanceState = new("Current P-state", iconName: "vcore");
    private readonly MetricViewModel _supportedStates = new("Supported P-states", iconName: "vcore");
    private readonly MetricViewModel _enforcedPower = new("Enforced GPU power limit", iconName: "power");
    private readonly MetricViewModel _defaultPower = new("Default GPU power limit", iconName: "power");
    private readonly MetricViewModel _powerRange = new("GPU power limit range", iconName: "power");
    private readonly MetricViewModel _currentLink = new("PCIe Link Speed", iconName: "pcie-link");
    private readonly MetricViewModel _maximumLink = new("Max PCIe Link Speed", iconName: "pcie-link");
    private readonly MetricViewModel _maximumGpuGeneration = new("GPU PCIe Gen", iconName: "pcie-link");
    private readonly MetricViewModel _receive = new("PCIe RX", iconName: "pcie-rx");
    private readonly MetricViewModel _transmit = new("PCIe TX", iconName: "pcie-tx");
    private readonly MetricViewModel _replays = new("PCIe replays", iconName: "pcie-replay");
    private readonly MetricViewModel _replayRollovers = new("PCIe replay rollovers", iconName: "pcie-rollover");

    public MetricViewModel ActiveBoostLimits { get; } = new("Active boost limits", iconName: "boost-limit");
    public IReadOnlyList<MetricViewModel> PowerAndPerformance { get; }
    public ObservableCollection<MetricViewModel> BoostLimits { get; } = [];
    public IReadOnlyList<MetricViewModel> Pcie { get; }

    public GpuDiagnosticsViewModel()
    {
        PowerAndPerformance = [_enforcedPower, _defaultPower, _powerRange, _performanceState, _supportedStates];

        BoostLimits.Add(_emptyBoostLimits);
        Pcie = [_currentLink, _maximumLink, _maximumGpuGeneration, _receive, _transmit, _replays, _replayRollovers];
    }

    internal void Apply(Func<SensorComponent, SensorMetric, int?, double?> read)
    {
        var active = PerformanceValue(SensorMetric.ActiveClockReasons);
        var supported = PerformanceValue(SensorMetric.SupportedClockReasons);
        ActiveBoostLimits.Value = FormatReasons(active, "None active");

        var visibleReasons = (NvidiaClockReason)(ulong)(supported ?? 0) | (NvidiaClockReason)(ulong)(active ?? 0);
        foreach (var reason in TimedReasons)
        {
            var seconds = PerformanceValue(SensorMetric.ClockLimitDuration, (int)reason);
            _boostLimitRows[reason].Value = seconds is { } duration ? $"{Number(duration, "0.###")} s" : "-";

            if (seconds.HasValue)
            {
                visibleReasons |= reason;
            }
        }

        UpdateBoostLimitRows(visibleReasons, supported.HasValue ? "None reported" : Unavailable);

        _performanceState.Value = PerformanceValue(SensorMetric.PerformanceState) is { } state ? $"P{(int)state}" : Unavailable;
        _supportedStates.Value = PerformanceValue(SensorMetric.SupportedPerformanceStates) is { } stateMask
            ? JoinOrUnavailable(Enumerable.Range(0, 16).Where(index => ((uint)stateMask & (1u << index)) != 0)
                .Select(index => $"P{index}"), ", ")
            : Unavailable;

        _enforcedPower.Value = Watts(Power(SensorMetric.EnforcedPowerLimit));
        _defaultPower.Value = Watts(Power(SensorMetric.DefaultPowerLimit));
        var minimum = Power(SensorMetric.MinimumPowerLimit);
        var maximum = Power(SensorMetric.MaximumPowerLimit);
        _powerRange.Value = minimum.HasValue || maximum.HasValue
            ? $"{Watts(minimum)} to {Watts(maximum)}" : Unavailable;

        _currentLink.Value = FormatLink(PcieValue(SensorMetric.LinkGeneration), PcieValue(SensorMetric.LinkWidth));
        _maximumLink.Value = FormatLink(PcieValue(SensorMetric.MaximumSystemLinkGeneration), PcieValue(SensorMetric.MaximumLinkWidth));
        _maximumGpuGeneration.Value = PcieValue(SensorMetric.MaximumGpuLinkGeneration) is { } generation
            ? $"Gen {(int)generation}" : Unavailable;
        _receive.Value = FormatThroughput(PcieValue(SensorMetric.ReceiveThroughput));
        _transmit.Value = FormatThroughput(PcieValue(SensorMetric.TransmitThroughput));
        _replays.Value = FormatCount(PcieValue(SensorMetric.ReplayCount));
        _replayRollovers.Value = FormatCount(PcieValue(SensorMetric.ReplayRolloverCount));

        return;

        double? PerformanceValue(SensorMetric metric, int? channel = null) => read(SensorComponent.GpuPerformance, metric, channel);
        double? Power(SensorMetric metric) => read(SensorComponent.GpuPowerLimits, metric, null);
        double? PcieValue(SensorMetric metric) => read(SensorComponent.GpuPcie, metric, null);
    }

    internal void Clear()
    {
        ActiveBoostLimits.Value = Unavailable;

        foreach (var metric in PowerAndPerformance.Concat(Pcie))
        {
            metric.Value = Unavailable;
        }

        UpdateBoostLimitRows(NvidiaClockReason.None, Unavailable);
    }

    private void UpdateBoostLimitRows(NvidiaClockReason visibleReasons, string emptyLabel)
    {
        _emptyBoostLimits.Label = emptyLabel;
        if (_visibleBoostLimits == visibleReasons)
        {
            return;
        }

        _visibleBoostLimits = visibleReasons;
        BoostLimits.Clear();

        if (visibleReasons == NvidiaClockReason.None)
        {
            BoostLimits.Add(_emptyBoostLimits);
            return;
        }

        foreach (var reason in ClockReasons)
        {
            if ((visibleReasons & reason) != 0)
            {
                BoostLimits.Add(_boostLimitRows[reason]);
                visibleReasons &= ~reason;
            }
        }

        if (visibleReasons != NvidiaClockReason.None)
        {
            BoostLimits.Add(_unknownBoostLimits);
        }
    }

    private static string FormatReasons(double? value, string empty)
    {
        if (value is null)
        {
            return Unavailable;
        }

        var mask = (NvidiaClockReason)(ulong)value.Value;
        if (mask == NvidiaClockReason.None)
        {
            return empty;
        }

        var names = new List<string>();
        foreach (var reason in ClockReasons)
        {
            if ((mask & reason) != 0)
            {
                names.Add(ReasonLabel(reason));
                mask &= ~reason;
            }
        }

        if (mask != NvidiaClockReason.None)
        {
            names.Add($"Unknown (0x{(ulong)mask:X})");
        }

        return string.Join(", ", names);
    }

    private static string ReasonLabel(NvidiaClockReason reason) => reason switch
    {
        NvidiaClockReason.Idle => "GPU idle",
        NvidiaClockReason.ApplicationClocks => "Application clocks",
        NvidiaClockReason.SoftwarePowerCap => "Software power cap",
        NvidiaClockReason.HardwareSlowdown => "Hardware slowdown",
        NvidiaClockReason.SyncBoost => "Sync Boost",
        NvidiaClockReason.SoftwareThermalSlowdown => "Software thermal slowdown",
        NvidiaClockReason.HardwareThermalSlowdown => "Hardware thermal slowdown",
        NvidiaClockReason.HardwarePowerBrake => "Hardware power brake",
        NvidiaClockReason.DisplayClocks => "Display clocks",
        NvidiaClockReason.BoardLimit => "Board limit",
        NvidiaClockReason.Reliability => "Reliability",
        _ => reason.ToString()
    };

    private static string FormatLink(double? generation, double? width)
    {
        var parts = new List<string>(2);
        if (generation is > 0)
        {
            parts.Add($"Gen {(int)generation.Value}");
        }

        if (width is > 0)
        {
            parts.Add($"×{(int)width.Value}");
        }

        double? transfersPerLane = generation switch
        {
            1 => 2.5,
            2 => 5,
            3 => 8,
            4 => 16,
            5 => 32,
            6 => 64,
            7 => 128,
            _ => null
        };

        var link = JoinOrUnavailable(parts, " ");
        return transfersPerLane is { } rate ? $"{link} · {Number(rate, "0.#")} GT/s per lane" : link;
    }

    private static string FormatThroughput(double? bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{Number(bytes.Value / 1_073_741_824, "0.##")} GiB/s",
        >= 1_048_576 => $"{Number(bytes.Value / 1_048_576, "0.##")} MiB/s",
        >= 1024 => $"{Number(bytes.Value / 1024, "0.##")} KiB/s",
        { } value => $"{Number(value, "0")} B/s",
        null => Unavailable
    };

    private static string Watts(double? value) => value is { } watts ? $"{Number(watts, "0.##")} W" : Unavailable;
    private static string FormatCount(double? value) => value is { } count ? Number(count, "0") : Unavailable;
    private static string Number(double value, string format) => value.ToString(format, CultureInfo.CurrentCulture);

    private static string JoinOrUnavailable(IEnumerable<string> values, string separator)
    {
        var text = string.Join(separator, values);
        return text.Length > 0 ? text : Unavailable;
    }
}
