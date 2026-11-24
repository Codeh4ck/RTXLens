using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Plugins.Asus.Contracts;
using RTXLens.Plugins.GenericNvidia.Contracts;
using RTXLens.Plugins.GenericNvidia.Contracts.Devices;

namespace RTXLens.Presentation.Performance;

public sealed partial class PerformanceRecorder
{
    private static readonly NvidiaClockReason[] Reasons = [.. Enum.GetValues<NvidiaClockReason>().Where(reason => reason != NvidiaClockReason.None)];
    private readonly HashSet<string> _astralDevices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _reportedReasons = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, SensorMetric), PerformanceGpuDetail> _liveDetails = [];
    private Dictionary<(string, SensorMetric), PerformanceGpuDetail> _sessionDetails = [];

    private Dictionary<string, string> GpuSources(SensorSnapshot snapshot)
    {
        var devices = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reading in snapshot.Sensors)
        {
            var id = reading.Identity;
            if (id.PluginId is NvidiaIdentity.Id or AsusIdentity.Id)
            {
                var first = devices.Add(id.DeviceId);
                if (id.PluginId == AsusIdentity.Id || first && NvidiaPciIdentity.TryParse(id.DeviceId, out var pci)
                    && NvidiaPciIdentity.IsAstral(pci.SubsystemId))
                {
                    _astralDevices.Add(id.DeviceId);
                }
            }
        }

        // Keep distinct cards identifiable when the monitored adapter changes mid-session.
        devices.UnionWith(_traces.Values.Where(state => !state.Imported &&
            state.Trace.Identity.PluginId is AsusIdentity.Id or NvidiaIdentity.Id).Select(state => state.Trace.DeviceId));
        return devices.ToDictionary(device => device, device =>
        {
            var brand = _astralDevices.Contains(device) ? "ROG Astral" : "NVIDIA";
            return devices.Count > 1 ? $"{brand} ({device})" : brand;
        });
    }

    private IEnumerable<SensorReading> ChartReadings(SensorSnapshot snapshot, Func<SensorReading, bool> usable)
    {
        // The simulation can supply an independent ASUS card with no NVIDIA stream.
        var simulatedNvidia = _mode == "simulation" ? snapshot.Sensors.Where(reading => reading.Identity.PluginId == NvidiaIdentity.Id)
            .Select(reading => reading.Identity.DeviceId).ToHashSet(StringComparer.Ordinal) : null;
        foreach (var reading in snapshot.Sensors)
        {
            if (reading.Identity is { PluginId: NvidiaIdentity.Id, Metric: SensorMetric.SupportedClockReasons or SensorMetric.ActiveClockReasons } id
                && usable(reading) && reading.Value >= 0)
            {
                _reportedReasons[id.DeviceId] = _reportedReasons.GetValueOrDefault(id.DeviceId) | (ulong)reading.Value.Value;
            }
        }

        foreach (var reading in snapshot.Sensors)
        {
            var id = reading.Identity;
            // These ASUS readings are copies of the standard NVIDIA sample for this same PCI device.
            // The board and connector channels have their own mappings and remain independently identifiable.
            if (id is { PluginId: AsusIdentity.Id, Component: SensorComponent.GpuCore or SensorComponent.GpuMemory or SensorComponent.GpuFan or SensorComponent.GpuHotspot }
                && (simulatedNvidia is null || simulatedNvidia.Contains(id.DeviceId)))
            {
                continue;
            }

            if (id is { PluginId: NvidiaIdentity.Id, Metric: SensorMetric.ActiveClockReasons, Channel: null })
            {
                if (!usable(reading) || reading.Value is not (>= 0 and double mask))
                {
                    continue;
                }

                var known = _reportedReasons.GetValueOrDefault(id.DeviceId);
                foreach (var reason in Reasons)
                {
                    if ((known & (ulong)reason) != 0)
                    {
                        yield return reading with
                        {
                            Identity = id with { Channel = (int)reason },
                            Value = ((ulong)mask & (ulong)reason) == 0 ? 0 : 1
                        };
                    }
                }
                continue;
            }
            yield return reading;
        }
    }

    private void UpdateGpuDetails(SensorSnapshot snapshot, Func<SensorReading, bool> usable)
    {
        _liveDetails.Clear();
        foreach (var reading in snapshot.Sensors)
        {
            var id = reading.Identity;
            if (id.PluginId != NvidiaIdentity.Id || !PerformanceGpuDetail.Supports(id.Metric) || !usable(reading))
            {
                continue;
            }

            var key = (id.DeviceId, id.Metric);
            var detail = new PerformanceGpuDetail(id.DeviceId, id.Metric, reading.Value!.Value);
            _liveDetails[key] = detail;
            if (_recording)
            {
                _sessionDetails[key] = detail;
            }
        }
    }
}
