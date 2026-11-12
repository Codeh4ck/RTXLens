using System.Text.Json;
using RTXLens.Client.Serialization;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Discovery;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Health;
using RTXLens.Contracts.Monitoring;
using RTXLens.Contracts.Sensors;
using RTXLens.Debugging.Catalog;
using RTXLens.Debugging.Models;
using RTXLens.Debugging.Processing;
using RTXLens.Debugging.ViewModels;
using RTXLens.Plugins.Asus.Contracts.Configuration;
using RTXLens.Plugins.Msi.Contracts.Configuration;
using RTXLens.Plugins.WireView.Contracts.Common;
using RTXLens.Presentation.Infrastructure;
using RTXLens.Warnings;

namespace RTXLens.Debugging;

public sealed class DebugSession : ObservableObject
{
    private readonly Core.ConnectorProfileResolver _connectorProfiles = SimulationConnectorProfiles.CreateResolver();

    private long _revision = 1;
    private long _sequence;
    private PluginCatalog _liveCatalog = new(1, []);
    private ConfigurationSnapshot _liveConfiguration = new(0, new ServiceConfiguration());
    private SensorSnapshot? _liveSnapshot;
    private DeviceConfig? _wireViewConfiguration;
    private string? _wireViewDeviceId;
    private readonly Dictionary<string, PluginSettings> _configurationOverrides = new(StringComparer.Ordinal);
    private readonly DebugAsusAlerts _asus = new();
    private bool _batch;
    private MonitoringOptions? _monitoringOverride;
    private RTXLens.Contracts.Warnings.WarningOptions? _warningOverride;
    private WarningEngine _warningEngine = new(DebugWarningCatalog.Registry);

    public event Action<bool>? Changed;

    public string LastNotification { get; private set => Set(ref field, value); } = "No debug notifications yet.";
    public DebugPluginViewModel[] Plugins { get; } = [new("wireview-pro2", "WireView Pro II"), new("asus-astral", "ROG Astral"), new("msi-psu", "MSI PSU"), new("generic-nvidia", "GenericNvidia")];

    public void ReportNotification(string title, string message) => LastNotification = "DEBUG · " + title + " · " + message;

    public bool Enabled
    {
        get;
        set
        {
            if (!Set(ref field, value))
            {
                return;
            }

            _wireViewConfiguration = null;
            _wireViewDeviceId = null;
            _asus.Reset();
            _warningEngine = new WarningEngine(DebugWarningCatalog.Registry);
            _revision++;
            Changed?.Invoke(true);
        }
    }

    public DebugSession()
    {
        foreach (var plugin in Plugins)
        {
            plugin.Edited += structural => Edited(structural, plugin.Id);
        }
    }

    private void Edited(bool structural, string? pluginId = null)
    {
        if (_batch)
        {
            return;
        }

        if (structural)
        {
            _revision++;
            if (pluginId == "asus-astral")
            {
                _asus.Reset();
            }
        }

        Changed?.Invoke(structural);
    }

    public void ReceiveCatalog(PluginCatalog catalog) => _liveCatalog = catalog;

    public void ReceiveConfiguration(ConfigurationSnapshot configuration)
    {
        _liveConfiguration = configuration;
        _revision++;
    }

    public void ReceiveSnapshot(SensorSnapshot? snapshot) => _liveSnapshot = snapshot;

    public void RestartAlerts()
    {
        _asus.Reset();
        Changed?.Invoke(false);
    }

    public void ResetPlugin(string id)
    {
        _batch = true;

        try
        {
            Plugins.Single(plugin => plugin.Id == id).Reset();
            _configurationOverrides.Remove(id);

            if (id == "wireview-pro2")
            {
                _wireViewConfiguration = null;
                _wireViewDeviceId = null;
            }
        }
        finally
        {
            _batch = false;
        }

        Edited(true, id);
    }

    public void ResetAll()
    {
        Enabled = false;
        _batch = true;

        try
        {
            foreach (var plugin in Plugins)
            {
                plugin.Reset();
            }

            _configurationOverrides.Clear();
            _monitoringOverride = null;
            _warningOverride = null;
            LastNotification = "No debug notifications yet.";
        }
        finally
        {
            _batch = false;
        }

        Edited(true, "asus-astral");
    }

    public DeviceConfig? GetSimulatedWireViewConfiguration()
    {
        var wireView = Plugins.Single(plugin => plugin.Id == "wireview-pro2");
        if (!Enabled || wireView is not { IsSimulated: true, Installed: true, Enabled: true, State: PluginState.Ready })
        {
            return null;
        }

        if (_wireViewConfiguration is null || _wireViewDeviceId != wireView.DeviceId)
        {
            _wireViewDeviceId = wireView.DeviceId;
            _wireViewConfiguration = DebugDeviceDefaults.WireView() with { EditToken = Guid.NewGuid().ToString("N") };
        }

        return _wireViewConfiguration;
    }

    public DeviceConfig ApplySimulatedWireViewConfiguration(DeviceConfig changes)
    {
        var current = GetSimulatedWireViewConfiguration()
            ?? throw new InvalidOperationException("The simulated WireView Pro II is not available.");
        if (changes.EditToken != current.EditToken)
        {
            throw new InvalidOperationException("Simulated settings changed. Refresh before applying your changes.");
        }

        WireViewSettingsValidation.Validate(current, changes, current.FirmwareVersion.GetValueOrDefault());

        _wireViewConfiguration = changes with { EditToken = Guid.NewGuid().ToString("N") };
        return _wireViewConfiguration;
    }

    public void SetConfiguration(ServiceConfiguration configuration, string version)
    {
        if (version != Version)
        {
            throw new InvalidOperationException("Debug configuration changed; reload before applying.");
        }

        if (configuration.Warnings is null || !configuration.Warnings.IsValid() || !configuration.Monitoring.IsValid() || configuration.Plugins.Any(pair => !pair.Value.IsValid()))
        {
            throw new ArgumentException("Invalid debug configuration.");
        }

        _batch = true;
        try
        {
            _configurationOverrides.Clear();

            foreach (var (id, settings) in configuration.Plugins)
            {
                _configurationOverrides[id] = settings;

                var plugin = Plugins.FirstOrDefault(plugin => plugin.Id == id);
                if (plugin is { IsSimulated: true })
                {
                    plugin.Enabled = settings.Enabled;
                }
            }

            _warningOverride = configuration.Warnings;
            _monitoringOverride = new MonitoringOptions
            {
                ConnectorProfiles = configuration.Monitoring.ConnectorProfiles,
                StreamIntervalMs = configuration.Monitoring.StreamIntervalMs,
                EventLogRetentionDays = configuration.Monitoring.EventLogRetentionDays,
                EventLogStorageMb = configuration.Monitoring.EventLogStorageMb,
                Selections = [.. configuration.Monitoring.Selections]
            };
        }
        finally
        {
            _batch = false;
        }

        Edited(true);
    }

    private string Version => "debug:" + _revision;

    public DebugFrame Compose(DateTimeOffset now)
    {
        if (!Enabled)
        {
            throw new InvalidOperationException("Debug simulation is disabled.");
        }

        var catalog = _liveCatalog.Plugins.Select(plugin => plugin with { Warnings = DebugWarningCatalog.ForPlugin(plugin.Id) }).ToList();
        var readings = _liveSnapshot?.Sensors.ToList() ?? [];
        var health = _liveSnapshot?.Plugins.ToList() ?? [];
        var alerts = _liveSnapshot?.Alerts.ToList() ?? [];
        var settings = new Dictionary<string, PluginSettings>(_liveConfiguration.Configuration.Plugins, StringComparer.Ordinal);

        foreach (var (id, value) in _configurationOverrides)
        {
            settings[id] = value;
        }

        foreach (var plugin in Plugins)
        {
            if (plugin.Mode == DebugSourceMode.Live)
            {
                continue;
            }

            var info = catalog.FirstOrDefault(info => info.Id == plugin.Id);
            if (plugin.Mode == DebugSourceMode.Override && info is null)
            {
                continue;
            }

            var baseline = readings.Where(reading => reading.Identity.PluginId == plugin.Id).ToArray();
            readings.RemoveAll(reading => reading.Identity.PluginId == plugin.Id);

            if (plugin.IsSimulated)
            {
                catalog.RemoveAll(catalogItem => catalogItem.Id == plugin.Id);
                health.RemoveAll(item => item.PluginId == plugin.Id);
                alerts.RemoveAll(item => item.PluginId == plugin.Id);

                if (!plugin.Installed)
                {
                    settings.Remove(plugin.Id);
                    continue;
                }

                var state = plugin.Enabled ? plugin.State : PluginState.Disabled;

                info = new PluginInfo(plugin.Id, plugin.Name, plugin.Enabled, state, state == PluginState.Ready ? now : null,
                    "DEBUG simulated plugin", Capabilities(plugin.Id)) { Warnings = DebugWarningCatalog.ForPlugin(plugin.Id) };

                catalog.Add(info);
                health.Add(new PluginHealth(plugin.Id, state, state == PluginState.Ready ? now : null, info.Message));

                settings[plugin.Id] = (settings.GetValueOrDefault(plugin.Id) ?? Defaults(plugin.Id)) with { Enabled = plugin.Enabled };

                if (state != PluginState.Ready)
                {
                    if (plugin.Id == "asus-astral")
                    {
                        _asus.Reset();
                    }

                    continue;
                }
            }

            var device = plugin.IsSimulated ? plugin.DeviceId : baseline.FirstOrDefault()?.Identity.DeviceId;
            if (device is null)
            {
                continue;
            }

            var stateHealth = health.FirstOrDefault(item => item.PluginId == plugin.Id);
            var staleAfterMs = settings.GetValueOrDefault(plugin.Id)?.StaleAfterMs ?? 5000;
            var ready = info!.Enabled && stateHealth is { State: PluginState.Ready, LastSuccessAt: { } success } &&
                        now - success <= TimeSpan.FromMilliseconds(staleAfterMs);

            if (!ready)
            {
                readings.AddRange(baseline);
                if (plugin.Id == "asus-astral")
                {
                    _asus.Reset();
                }

                continue;
            }

            var effective = DebugReadingBuilder.Build(plugin, baseline, device, now).ToList();
            if (plugin is { IsSimulated: true, Id: "msi-psu" } && _configurationOverrides.TryGetValue(plugin.Id, out var msiSettings))
            {
                var options = msiSettings.Options?.Deserialize(ClientJsonContext.Default.MsiOptions) ?? new MsiOptions();
                if (options.FanControl != MsiFanControl.Unmanaged)
                {
                    foreach (var metric in plugin.Metrics.Where(metric => metric is
                                 { Mode: DebugMetricMode.Live, Definition.Metric: SensorMetric.FanAutomatic or SensorMetric.FanDuty }))
                    {
                        var index = effective.FindIndex(reading => metric.Definition.Matches(reading.Identity));
                        effective[index] = effective[index] with
                        {
                            Value = metric.Definition.Metric == SensorMetric.FanAutomatic
                                ? options.FanControl == MsiFanControl.Automatic ? 1 : 0
                                : options.FanControl == MsiFanControl.Manual
                                    ? options.FanDutyPercent
                                    : 0
                        };
                    }
                }
            }

            if (plugin.Id == "asus-astral" && (plugin.IsSimulated || plugin.Metrics.Any(metric => metric.Mode != DebugMetricMode.Live
                                                                                                  && (metric.Definition.Metric is SensorMetric
                                                                                                              .ImbalanceDetected
                                                                                                          or SensorMetric.ImbalancePercentage
                                                                                                          or SensorMetric.OvercurrentDetected
                                                                                                      || metric.Definition is
                                                                                                      {
                                                                                                          Component: SensorComponent.GpuPin,
                                                                                                          Metric: SensorMetric.Current
                                                                                                      }))))
            {
                alerts.RemoveAll(alert => alert.PluginId == plugin.Id);

                var options = settings.GetValueOrDefault(plugin.Id)?.Options?.Deserialize(ClientJsonContext.Default.AsusOptions) ?? new AsusOptions();
                alerts.AddRange(_asus.Evaluate(plugin, effective, device, options, now, staleAfterMs));
            }

            readings.AddRange(effective);
        }

        var sourceMonitoring = _monitoringOverride ?? _liveConfiguration.Configuration.Monitoring;

        var monitoring = new MonitoringOptions
        {
            ConnectorProfiles = sourceMonitoring.ConnectorProfiles,
            StreamIntervalMs = sourceMonitoring.StreamIntervalMs,
            EventLogRetentionDays = sourceMonitoring.EventLogRetentionDays,
            EventLogStorageMb = sourceMonitoring.EventLogStorageMb,
            Selections = [.. sourceMonitoring.Selections]
        };

        var configuration = new ConfigurationSnapshot(_revision, new ServiceConfiguration { Plugins = settings, Monitoring = monitoring, Warnings = _warningOverride ?? _liveConfiguration.Configuration.Warnings },
            Version: Version);

        var usable = readings.Where(reading =>
                reading.Quality == ReadingQuality.Valid &&
                health.Any(plugin => plugin.PluginId == reading.Identity.PluginId && plugin.State == PluginState.Ready) && now - reading.ObservedAt <=
                TimeSpan.FromMilliseconds(settings.GetValueOrDefault(reading.Identity.PluginId)?.StaleAfterMs ?? 5000))
            .ToDictionary(reading => reading.Identity.Key, StringComparer.Ordinal);

        var selections = monitoring.Selections.Select(selection => Core.SourceResolver.Resolve(selection, usable)).ToArray();
        var snapshot = new SensorSnapshot(1, ++_sequence, now, [.. readings], [.. health], selections, _revision) { Alerts = [.. alerts] };

        snapshot = _connectorProfiles.Resolve(snapshot, configuration.Configuration);
        snapshot = snapshot with { Warnings = _warningEngine.Evaluate(snapshot, configuration.Configuration, now) };

        foreach (var plugin in Plugins)
        {
            foreach (var metric in plugin.Metrics)
            {
                var live = _liveSnapshot?.Sensors.FirstOrDefault(reading =>
                    reading.Identity.PluginId == plugin.Id && metric.Definition.Matches(reading.Identity));
                metric.UpdateLive(live is { Quality: ReadingQuality.Valid } ? live.Value : null);

                var effective = readings.FirstOrDefault(reading =>
                    reading.Identity.PluginId == plugin.Id && metric.Definition.Matches(reading.Identity));
                metric.UpdateEffective(effective?.Value, effective?.Quality.ToString() ?? DebugMetricViewModel.NotReported);
            }
        }

        return new DebugFrame(new PluginCatalog(1, [.. catalog]), configuration, snapshot);
    }

    private static PluginSettings Defaults(string id) => new()
    {
        Options = id switch
        {
            "asus-astral" => JsonSerializer.SerializeToElement(new AsusOptions(), ClientJsonContext.Default.AsusOptions),
            "generic-nvidia" => JsonSerializer.SerializeToElement(new RTXLens.Plugins.GenericNvidia.Contracts.Configuration.GenericNvidiaOptions(), ClientJsonContext.Default.GenericNvidiaOptions),
            "msi-psu" => JsonSerializer.SerializeToElement(new MsiOptions(), ClientJsonContext.Default.MsiOptions),
            _ => null
        }
    };

    private static PluginCapability[] Capabilities(string id) => id == "wireview-pro2"
        ?
        [
            PluginCapability.Telemetry, PluginCapability.Configuration, PluginCapability.Restart, PluginCapability.DeviceConfigurationRead,
            PluginCapability.FaultClear
        ]
        : [PluginCapability.Telemetry, PluginCapability.Configuration, PluginCapability.Restart, PluginCapability.DeviceSelection];
}