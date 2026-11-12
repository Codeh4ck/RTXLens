using System.Collections.ObjectModel;
using System.Globalization;
using RTXLens.Contracts.Enums;
using RTXLens.Presentation.Infrastructure;
using RTXLens.Plugins.GenericNvidia.Contracts.Devices;

namespace RTXLens.Presentation.Telemetry;

public sealed class GpuDiagnosticsViewModel : ObservableObject
{
    private const string Unavailable = "Unavailable";
    private static readonly NvidiaClockReason[] ClockReasons =
    [
        .. Enum.GetValues<NvidiaClockReason>()
            .Where(reason => reason != NvidiaClockReason.None)
    ];
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

    public MetricViewModel ActiveBoostLimits { get; } = new("Active boost limits", iconName: "boost-limit");
    public MetricViewModel CurrentPState { get; } = new("Current P-state", iconName: "vcore");
    public MetricViewModel SupportedPStates { get; } = new("Supported P-states", iconName: "vcore");
    public MetricViewModel EnforcedPower { get; } = new("Enforced GPU power limit", iconName: "power");
    public MetricViewModel DefaultPower { get; } = new("Default GPU power limit", iconName: "power");
    public MetricViewModel CurrentLink { get; } = new("PCIe Link Speed", iconName: "pcie-link");
    public MetricViewModel MaximumLink { get; } = new("Max PCIe Link Speed", iconName: "pcie-link");
    public MetricViewModel MaximumGpuGeneration { get; } = new("GPU PCIe Gen", iconName: "pcie-link");
    public MetricViewModel ReceiveThroughput { get; } = new("PCIe RX", iconName: "pcie-rx");
    public MetricViewModel TransmitThroughput { get; } = new("PCIe TX", iconName: "pcie-tx");
    public MetricViewModel Replays { get; } = new("PCIe replays", iconName: "pcie-replay");
    public MetricViewModel ReplayRollovers { get; } = new("PCIe replay rollovers", iconName: "pcie-rollover");

    public string MinimumPowerText { get; private set => Set(ref field, value); } = Unavailable;
    public string MaximumPowerText { get; private set => Set(ref field, value); } = Unavailable;
    public bool HasPowerScale { get; private set => Set(ref field, value); }
    public (double? Enforced, double? Default) PowerLimitPositions { get; private set => Set(ref field, value); }
    public ObservableCollection<MetricViewModel> BoostLimits { get; } = [];

    private readonly MetricViewModel[] _metrics;

    public GpuDiagnosticsViewModel()
    {
        BoostLimits.Add(_emptyBoostLimits);
        _metrics = [ActiveBoostLimits, CurrentPState, SupportedPStates, CurrentLink, MaximumLink, MaximumGpuGeneration,
            ReceiveThroughput, TransmitThroughput, Replays, ReplayRollovers];
    }

    internal void Apply(Func<SensorComponent, SensorMetric, int?, double?> read)
    {
        var active = PerformanceValue(SensorMetric.ActiveClockReasons);
        var supported = PerformanceValue(SensorMetric.SupportedClockReasons);
        ActiveBoostLimits.Value = FormatReasons(active, "None active");

        var activeReasons = (NvidiaClockReason)(ulong)(active ?? 0);
        ActiveBoostLimits.IsWarning = (activeReasons & (NvidiaClockReason.SoftwareThermalSlowdown
            | NvidiaClockReason.HardwareThermalSlowdown | NvidiaClockReason.HardwarePowerBrake)) != 0;

        var visibleReasons = (NvidiaClockReason)(ulong)(supported ?? 0) | activeReasons;
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

        CurrentPState.Value = PerformanceValue(SensorMetric.PerformanceState) is { } state ? $"P{(int)state}" : Unavailable;
        SupportedPStates.Value = PerformanceValue(SensorMetric.SupportedPerformanceStates) is { } stateMask
            ? JoinOrUnavailable(Enumerable.Range(0, 16).Where(index => ((uint)stateMask & (1u << index)) != 0)
                .Select(index => $"P{index}"), ", ")
            : Unavailable;

        UpdatePowerLimits(Power(SensorMetric.EnforcedPowerLimit), Power(SensorMetric.DefaultPowerLimit),
            Power(SensorMetric.MinimumPowerLimit), Power(SensorMetric.MaximumPowerLimit));

        CurrentLink.Value = FormatLink(PcieValue(SensorMetric.LinkGeneration), PcieValue(SensorMetric.LinkWidth));
        MaximumLink.Value = FormatLink(PcieValue(SensorMetric.MaximumSystemLinkGeneration), PcieValue(SensorMetric.MaximumLinkWidth));
        MaximumGpuGeneration.Value = PcieValue(SensorMetric.MaximumGpuLinkGeneration) is { } generation
            ? $"Gen {(int)generation}" : Unavailable;
        ReceiveThroughput.Value = FormatThroughput(PcieValue(SensorMetric.ReceiveThroughput));
        TransmitThroughput.Value = FormatThroughput(PcieValue(SensorMetric.TransmitThroughput));
        Replays.Value = FormatCount(PcieValue(SensorMetric.ReplayCount));
        ReplayRollovers.Value = FormatCount(PcieValue(SensorMetric.ReplayRolloverCount));

        return;

        double? PerformanceValue(SensorMetric metric, int? channel = null) => read(SensorComponent.GpuPerformance, metric, channel);
        double? Power(SensorMetric metric) => read(SensorComponent.GpuPowerLimits, metric, null);
        double? PcieValue(SensorMetric metric) => read(SensorComponent.GpuPcie, metric, null);
    }

    internal void Clear()
    {
        foreach (var metric in _metrics)
        {
            metric.Value = Unavailable;
            metric.IsWarning = false;
        }

        UpdatePowerLimits(null, null, null, null);
        UpdateBoostLimitRows(NvidiaClockReason.None, Unavailable);
    }

    private void UpdatePowerLimits(double? enforced, double? defaultLimit, double? minimum, double? maximum)
    {
        EnforcedPower.Value = Watts(enforced);
        DefaultPower.Value = Watts(defaultLimit);
        MinimumPowerText = Watts(minimum);
        MaximumPowerText = Watts(maximum);

        HasPowerScale = minimum.HasValue && maximum > minimum;
        PowerLimitPositions = (Position(enforced), Position(defaultLimit));

        double? Position(double? limit) => HasPowerScale && limit >= minimum && limit <= maximum
            ? (limit - minimum) / (maximum - minimum)
            : null;
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
