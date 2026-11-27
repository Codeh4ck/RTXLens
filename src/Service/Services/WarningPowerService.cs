using System.Collections.Concurrent;
using System.Text.Json;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Mobile;
using RTXLens.Plugins.GenericNvidia.Contracts;
using RTXLens.Contracts.Warnings;
using RTXLens.Core.Diagnostics;
using RTXLens.Hardware.Nvidia.Diagnostics;
using RTXLens.Plugins.Asus.Contracts;
using RTXLens.Service.Configuration;
using RTXLens.Warnings;

namespace RTXLens.Service.Services;

internal sealed record PowerLease(string GpuId, uint Original, uint Applied, string[] WarningIds, bool Manual, uint? PreviousApplied = null,
    uint? OriginalEnforced = null, bool Confirmed = false, bool Restoring = false, bool RestoreNeedsEnforced = false)
{
    // Drivers can report the original configured limit throughout a successful cap.
    // That value alone is never evidence of another tuning tool changing the setting.
    public bool IsUnrelatedSetting(uint configured) =>
        configured != Original && configured != Applied && configured != PreviousApplied;

    public bool ReductionVisible(uint configured, uint? enforced) =>
        configured == Applied || (enforced == Applied && OriginalEnforced != Applied);

    public bool RestorationVisible(uint configured, uint? enforced) =>
        configured == Original && (enforced is { } watts
            ? watts > Applied || (!RestoreNeedsEnforced && watts == OriginalEnforced)
            : !RestoreNeedsEnforced);
}
internal sealed record PowerJournal(DateTimeOffset Boot, PowerLease[] Leases);

internal sealed class WarningPowerService(RuntimeConfigurationFile configuration, ProfileGpuDiscovery discovery,
    TimeProvider clock, ILogger<WarningPowerService> logger, Func<string, INvidiaPowerControl>? openControl = null) : BackgroundService
{
    private sealed class MobileDemand(string key, string instance, string episode, GpuPowerReaction reaction)
    {
        public string Key { get; } = key;
        public string Instance { get; } = instance;
        public string Episode { get; } = episode;
        public GpuPowerReaction Reaction { get; } = reaction;
        public volatile MobileWarningActionResult Result = new("requested");
        public volatile bool MayHaveWritten;
    }

    private sealed record Demand(string Id, GpuPowerReaction Reaction, WarningPhase? Phase, MobileDemand? Mobile = null);
    private sealed record Frame(long Time, Demand[] Automatic, WarningSnapshot Warnings, Demand[] Demands);

    private readonly string _path = Path.Combine(Path.GetDirectoryName(configuration.Path)!, "warning-power-limits.json");
    private readonly DateTimeOffset _boot = clock.GetUtcNow() - TimeSpan.FromMilliseconds(Environment.TickCount64);
    private readonly Dictionary<string, PowerLease> _leases = [];
    private readonly Dictionary<string, INvidiaPowerControl> _sessions = [];
    private readonly Func<string, INvidiaPowerControl> _openControl = openControl ?? NvmlPowerControl.Open;
    private long _driverGeneration;
    private readonly Dictionary<string, DateTimeOffset> _retry = [];
    private readonly HashSet<string> _alreadyBelow = [];
    private readonly Dictionary<string, HashSet<string>> _overridden = [];
    private readonly ConcurrentDictionary<string, byte> _restore = new();
    private readonly Lock _frames = new();
    private readonly Dictionary<string, MobileDemand> _mobile = [];
    private volatile Frame? _latest;
    private volatile HeldGpuLimit[] _held = [];
    private volatile bool _journalFailed;

    public HeldGpuLimit[] Held => _held;

    public PowerTarget[] Targets()
    {
        var targets = new List<PowerTarget>();
        foreach (var gpu in discovery.GetCachedDevices())
        {
            try
            {
                using var control = _openControl(gpu.Id);
                var range = control.Read();
                targets.Add(new(gpu.Id, gpu.Name, range.Minimum, range.Maximum));
            }
            catch (Exception error)
            {
                logger.LogDebug(error, "Power limit unavailable for {Gpu}", gpu.Id);
            }
        }
        return [.. targets];
    }

    public void RequestRestore(string gpu)
    {
        if (!Held.Any(item => item.GpuId == gpu))
        {
            throw new ArgumentException("No RTX Lens power limit is held on this GPU.");
        }

        _restore[gpu] = 0;
    }

    // The snapshot publisher only copies managed data. All driver and disk I/O runs on the worker.
    public void Observe(DeviceWarningProfile[] profiles, IReadOnlyList<ConfiguredWarning> observations, WarningSnapshot warnings)
    {
        var phases = warnings.States.ToDictionary(w => w.Id, w => w.Phase);
        var demands = new List<Demand>();
        foreach (var profile in profiles.Where(p => p.Enabled && p.PowerReduction is not null))
        {
            var reaction = profile.PowerReduction!;
            foreach (var configured in observations.Where(w => w.Observation.ProfileId == profile.Id))
            {
                var observation = configured.Observation;
                if (profile.PluginId == AsusIdentity.Id && observation.DeviceId != reaction.GpuId)
                {
                    continue;
                }

                WarningPhase? phase = phases.TryGetValue(observation.Id, out var state) ? state :
                    observation.Available && !observation.Active ? null : WarningPhase.Unavailable;
                demands.Add(new(observation.Id, reaction, phase));
            }
        }
        lock (_frames)
        {
            var active = warnings.States.Select(warning => warning.EpisodeId).ToHashSet(StringComparer.Ordinal);
            foreach (var request in _mobile.Values.Where(request =>
                (request.Instance != warnings.InstanceId || !active.Contains(request.Episode))
                && !Held.Any(lease => lease.GpuId == request.Reaction.GpuId)).ToArray())
                _mobile.Remove(request.Key);

            PublishFrame(clock.GetTimestamp(), [.. demands], warnings);
        }
    }

    public WarningState? ActiveMobileWarning(string? instance, string? episode)
    {
        var frame = _latest;
        return frame is not null && frame.Warnings.InstanceId == instance
            && clock.GetElapsedTime(frame.Time) <= TimeSpan.FromSeconds(2)
            ? frame.Warnings.States.FirstOrDefault(warning => warning.EpisodeId == episode && warning.Phase == WarningPhase.Active
                && !warning.TypeId.StartsWith("debug/", StringComparison.OrdinalIgnoreCase))
            : null;
    }

    public static bool MatchesMobileGpu(WarningState warning, string gpu) =>
        warning.PluginId is not (AsusIdentity.Id or NvidiaIdentity.Id) || warning.DeviceId == gpu;

    public MobileWarningActionResult RequestMobilePower(Guid phone, string instance, string episode, string gpu, uint milliwatts,
        bool statusOnly = false)
    {
        lock (_frames)
        {
            if (_journalFailed)
                return new("failed", Message: "GPU protection is unavailable. Check the RTX Lens event log.");

            var warning = ActiveMobileWarning(instance, episode);
            if (warning is null)
                return new("inactive", Message: "This warning is no longer active.");
            if (!MatchesMobileGpu(warning, gpu))
                return new("unavailable", Message: "This warning applies to a different GPU.");

            var key = $"mobile/{phone:N}/{instance}/{episode}";
            if (_mobile.TryGetValue(key, out var existing))
                return existing.Result;
            if (statusOnly)
                return new("inactive", Message: "This power request is no longer available.");
            if (_mobile.Count >= 512)
                return new("unavailable", Message: "The service has too many outstanding power requests.");

            var request = new MobileDemand(key, instance, episode, new(gpu, milliwatts));
            _mobile.Add(key, request);
            var frame = _latest!;
            PublishFrame(frame.Time, frame.Automatic, frame.Warnings);
            Log("mobile-cap-request", $"Paired phone requested a {Watts(milliwatts)} cap for {warning.Title}.", gpu, LogLevel.Warning);
            return request.Result;
        }
    }

    private void PublishFrame(long time, Demand[] automatic, WarningSnapshot warnings)
    {
        if (_mobile.Count == 0)
        {
            _latest = new(time, automatic, warnings, automatic);
            return;
        }

        var phases = warnings.States.ToDictionary(warning => warning.EpisodeId, warning => warning.Phase);
        var mobile = _mobile.Values.Where(request => request.Result.State != "failed" || request.MayHaveWritten)
            .Select(request => new Demand(request.Key, request.Reaction,
                request.Instance == warnings.InstanceId
                    ? phases.TryGetValue(request.Episode, out var phase) ? phase : null
                    : WarningPhase.Unavailable, request));
        _latest = new(time, automatic, warnings, [.. automatic, .. mobile]);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            if (File.Exists(_path))
            {
                var journal = JsonSerializer.Deserialize(File.ReadAllText(_path), AppJsonContext.Default.PowerJournal)
                    ?? throw new InvalidDataException("Empty power journal.");
                if ((journal.Boot - _boot).Duration() < TimeSpan.FromSeconds(3))
                {
                    foreach (var lease in journal.Leases)
                    {
                        _leases.Add(lease.GpuId, lease with { Manual = true });
                    }
                }

                PublishHeld();
            }
        }
        catch (Exception error)
        {
            _journalFailed = true;
            logger.LogActivity(LogLevel.Error, "power.journal", "GPU protection", "Could not read the power-limit journal. GPU reactions are suspended.", exception: error);
            return;
        }

        try
        {
            await MonitorAsync(stoppingToken);
        }
        finally
        {
            foreach (var session in _sessions.Values)
            {
                session.Dispose();
            }
        }
    }

    private async Task MonitorAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var frame = _latest;
            if (frame is null || clock.GetElapsedTime(frame.Time) > TimeSpan.FromSeconds(2))
            {
                continue;
            }

            var devices = frame.Demands.Where(d => d.Phase == WarningPhase.Active).Select(d => d.Reaction.GpuId)
                .Concat(_leases.Keys).Concat(_overridden.Keys).Distinct().ToArray();
            var generation = discovery.DriverGeneration;
            if (generation != _driverGeneration)
            {
                _driverGeneration = generation;
                foreach (var gpu in _sessions.Keys.ToArray())
                {
                    CloseSession(gpu);
                }

                foreach (var lease in _leases.Values.ToArray())
                {
                    _leases[lease.GpuId] = lease with { Manual = true };
                }

                if (_leases.Count > 0)
                {
                    Log("driver-reset", "GPU driver session changed. Verify the held limits before manual restoration.", "NVIDIA");
                }
            }
            _alreadyBelow.IntersectWith(devices);
            foreach (var idle in _sessions.Keys.Except(devices).ToArray())
            {
                CloseSession(idle);
            }

            foreach (var gpu in devices)
            {
                if (_retry.GetValueOrDefault(gpu) > clock.GetUtcNow())
                {
                    continue;
                }

                try
                {
                    Process(gpu, frame);
                    _retry.Remove(gpu);
                }
                catch (Exception error)
                {
                    CloseSession(gpu);
                    _retry[gpu] = clock.GetUtcNow().AddSeconds(10);
                    // A transient read or write error is retryable. A detected driver reset
                    // still requires manual restoration and a third-party setting relinquishes ownership.
                    foreach (var demand in frame.Demands.Where(demand => demand.Reaction.GpuId == gpu))
                    {
                        if (demand.Mobile is { MayHaveWritten: false, Result.State: "requested" } mobile)
                            mobile.Result = new("failed", Message: "The NVIDIA driver could not apply the power limit. Check the RTX Lens event log.");
                    }
                    logger.LogActivity(LogLevel.Warning, "power.failed", "GPU protection", error.Message, gpu);
                }
            }
        }
    }

    private void Process(string gpu, Frame frame)
    {
        var demands = frame.Demands.Where(d => d.Reaction.GpuId == gpu).ToArray();
        var active = demands.Where(d => d.Phase == WarningPhase.Active && d.Mobile?.Result.State != "failed").ToArray();
        if (_overridden.TryGetValue(gpu, out var suppressed))
        {
            suppressed.UnionWith(active.Select(d => d.Id));
            suppressed.IntersectWith(demands.Where(d => d.Phase is not null).Select(d => d.Id));
            if (suppressed.Count > 0)
            {
                SetMobileResult(active, new("failed", Message: "Another application changed the GPU power limit."));
                return;
            }

            _overridden.Remove(gpu);
        }

        _leases.TryGetValue(gpu, out var lease);
        if (active.Length == 0 && lease is null)
        {
            return;
        }

        if (!_sessions.TryGetValue(gpu, out var control))
        {
            control = _openControl(gpu);
            _sessions.Add(gpu, control);
        }
        var range = control.Read();
        if (lease is not null && lease.IsUnrelatedSetting(range.Requested))
        {
            _overridden[gpu] = active.Select(d => d.Id).ToHashSet();
            Remove(gpu);
            SetMobileResult(active, new("failed", Message: "Another application changed the GPU power limit."));
            Log("external", $"Configured limit changed to {Watts(range.Requested)}. Released the saved {Watts(lease.Original)} restore target.", gpu);
            return;
        }

        var requestedRestore = _restore.TryRemove(gpu, out _);
        if (lease is { Restoring: true } && lease.RestorationVisible(range.Requested, range.Enforced))
        {
            Remove(gpu);
            Log("restored", $"Successfully restored original power to {Watts(lease.Original)}.", gpu);
            // A fresh warning may have arrived during restoration. Process its new cap on the next frame.
            return;
        }
        if (lease is { Restoring: false, Confirmed: false } && lease.ReductionVisible(range.Requested, range.Enforced))
        {
            lease = lease with { Confirmed = true, PreviousApplied = range.Requested == lease.Applied ? null : lease.PreviousApplied };
            Save(lease);
            Log("reduced", $"Nvidia driver protection power limit ({Watts(lease.Applied)}) has been set.", gpu, LogLevel.Warning);
        }
        if (active.Length > 0)
        {
            foreach (var demand in active.Where(d => d.Mobile is not null
                && (d.Reaction.TargetMilliwatts < range.Minimum || d.Reaction.TargetMilliwatts > range.Maximum)))
                demand.Mobile!.Result = new("failed", Message: "The saved mobile power limit is outside the current GPU range.");

            var inRange = active.Where(d => d.Reaction.TargetMilliwatts >= range.Minimum && d.Reaction.TargetMilliwatts <= range.Maximum).ToArray();
            if (inRange.Length == 0)
            {
                if (active.Any(d => d.Mobile is null))
                    throw new InvalidOperationException("Saved emergency limit is outside the current driver range.");
                return;
            }

            active = inRange;
            var target = active.Min(d => d.Reaction.TargetMilliwatts);

            if (lease is null && range.Requested <= target)
            {
                if ((range.Enforced ?? range.Requested) <= target)
                    SetMobileResult(active, new("applied", range.Enforced ?? range.Requested));
                if (_alreadyBelow.Add(gpu))
                {
                    Log("already-limited", $"Current configured limit is {Watts(range.Requested)}, already at or below {Watts(target)}.", gpu);
                }

                return;
            }
            _alreadyBelow.Remove(gpu);
            var cap = Math.Min(lease?.Applied ?? range.Requested, target);
            var changing = lease is null || lease.Restoring || cap != lease.Applied;
            var next = new PowerLease(gpu, lease?.Original ?? range.Requested, cap,
                [.. (lease?.WarningIds ?? []).Concat(active.Select(d => d.Id)).Distinct().Order()],
                lease?.Manual == true || active.Any(d => !d.Reaction.AutoRestore),
                changing ? lease?.Applied : lease?.PreviousApplied,
                lease is null ? range.Enforced : lease.OriginalEnforced, !changing && lease!.Confirmed);

            if (changing || lease!.Manual != next.Manual || !lease.WarningIds.SequenceEqual(next.WarningIds))
            {
                if (!StillCurrent(frame, gpu))
                {
                    return;
                }

                Save(next);
                if (changing)
                {
                    try
                    {
                        if (!StillCurrent(frame, gpu))
                        {
                            if (lease is null) Remove(gpu);
                            else Save(lease);
                            return;
                        }

                        Log("cap-requested", $"GPU protection triggered. Requesting Nvidia driver to set power limit to {Watts(cap)}.", gpu, LogLevel.Warning);
                        control.Change(range.Requested, cap);
                        foreach (var demand in active)
                        {
                            if (demand.Mobile is { } mobile)
                                mobile.MayHaveWritten = true;
                        }
                    }
                    catch
                    {
                        // The setter did not accept this request. Reinstate any previous
                        // cap's journal so a rejected stricter cap cannot erase its restore point.
                        if (lease is null)
                        {
                            Remove(gpu);
                        }
                        else
                        {
                            Save(lease);
                        }

                        throw;
                    }
                }
            }
            if (!changing && next.Confirmed && (range.Enforced ?? range.Requested) <= cap)
                SetMobileResult(active, new("applied", range.Enforced ?? range.Requested));

            if (requestedRestore)
            {
                Log("held", "A warning is active. Keeping the GPU power limit.", gpu);
            }

            return;
        }

        var clear = demands.Where(d => d.Phase is null).Select(d => d.Id).ToHashSet();
        var blocking = demands.Any(d => d.Phase is not null && lease!.WarningIds.Contains(d.Id));
        if (blocking || (!requestedRestore && (lease!.Manual || lease.Restoring || !lease.WarningIds.All(clear.Contains))))
        {
            if (requestedRestore)
            {
                Log("held", "Warning recovery is incomplete. Keeping the GPU power limit.", gpu);
            }

            return;
        }
        if (!StillCurrent(frame, gpu))
        {
            if (requestedRestore)
            {
                _restore[gpu] = 0;
            }

            return;
        }

        // Even when the configured getter still reads the original value, send the restore:
        // the GPU may be physically enforcing our lower cap on a different power-control path.
        var restoring = lease! with { Restoring = true, RestoreNeedsEnforced = range.Requested == lease.Original };
        Save(restoring);
        var reason = requestedRestore && !lease.WarningIds.All(clear.Contains)
            ? "Manual GPU power restoration requested."
            : "GPU protection no longer required, warning condition cleared.";
        Log("restore-requested", $"{reason} Requesting Nvidia driver to restore power limit to {Watts(restoring.Original)}.", gpu);
        try
        {
            control.Change(range.Requested, restoring.Original);
        }
        catch
        {
            Save(lease);
            if (requestedRestore)
            {
                _restore[gpu] = 0;
            }

            throw;
        }
    }

    private static void SetMobileResult(Demand[] demands, MobileWarningActionResult result)
    {
        foreach (var demand in demands)
        {
            if (demand.Mobile is { } mobile)
                mobile.Result = result;
        }
    }

    private bool StillCurrent(Frame frame, string gpu)
    {
        var latest = _latest;
        return latest is not null && clock.GetElapsedTime(latest.Time) <= TimeSpan.FromSeconds(2)
            && frame.Demands.Where(d => d.Reaction.GpuId == gpu).SequenceEqual(latest.Demands.Where(d => d.Reaction.GpuId == gpu));
    }

    private void Save(PowerLease lease)
    {
        Write(_leases.Values.Where(l => l.GpuId != lease.GpuId).Append(lease));
        _leases[lease.GpuId] = lease;
        PublishHeld();
    }

    private void Remove(string gpu)
    {
        Write(_leases.Values.Where(l => l.GpuId != gpu));
        _leases.Remove(gpu);
        _restore.TryRemove(gpu, out _);
        PublishHeld();
    }

    private void Write(IEnumerable<PowerLease> leases)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        SettingsProfileStore.WriteAtomically(_path, JsonSerializer.Serialize(new PowerJournal(_boot, [.. leases]), AppJsonContext.Default.PowerJournal));
    }

    private void CloseSession(string gpu)
    {
        if (_sessions.Remove(gpu, out var session))
        {
            session.Dispose();
        }
    }

    private void PublishHeld() => _held = [.. _leases.Values.Select(l => new HeldGpuLimit(l.GpuId, l.Original, l.Applied, l.Restoring))];
    private static string Watts(uint value) => $"{value / 1000m:0.###} W";
    private void Log(string action, string message, string gpu, LogLevel level = LogLevel.Information) =>
        logger.LogActivity(level, "power." + action, "GPU protection", message, gpu);
}
