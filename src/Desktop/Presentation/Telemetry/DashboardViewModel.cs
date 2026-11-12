using System.Collections.ObjectModel;
using System.Text.Json;
using RTXLens.Client.Serialization;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Discovery;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Presentation.Infrastructure;
using RTXLens.Presentation.Settings;

namespace RTXLens.Presentation.Telemetry;

public sealed class DashboardViewModel : ObservableObject
{
    private ConfigurationSnapshot? _configuration;
    private SensorSnapshot? _lastSnapshot;
    private AppPreferences _preferences = new();

    public ObservableCollection<Contracts.Warnings.WarningState> CurrentWarnings { get; } = [];
    public string WarningSummary { get; private set; } = "Waiting for shared warning state";
    public ObservableCollection<HardwarePanelViewModel> Panels { get; } = [];
    public GpuStatusViewModel GpuStatus { get; } = new();
    public ObservableCollection<HardwarePanelViewModel> ReportingPanels { get; } = [];
    public bool HasAlternativeGpuStatus { get; private set; }
    public string PluginWarning
    {
        get;
        private set
        {
            if (Set(ref field, value))
            {
                Raise(nameof(HasPluginWarning));
            }
        }
    } = string.Empty;
    public bool HasPluginWarning => !string.IsNullOrEmpty(PluginWarning);
    public string Connection
    {
        get;
        set => Set(ref field, value);
    } = "Connecting…";
    public string Summary
    {
        get;
        private set => Set(ref field, value);
    } = "Discovering plugins";
    public HardwarePanelViewModel? SelectedPanel
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            if (field is not null)
            {
                field.IsSelected = false;
            }

            if (Set(ref field, value) && value is not null)
            {
                value.IsSelected = true;
            }
        }
    }

    public void SetCatalog(PluginCatalog catalog)
    {
        foreach (var panel in Panels.Where(panel => catalog.Plugins.All(plugin => plugin.Id != panel.Id)).ToArray())
        {
            Panels.Remove(panel);
        }

        foreach (var plugin in catalog.Plugins)
        {
            if (Panels.All(panel => panel.Id != plugin.Id))
            {
                Panels.Add(new HardwarePanelViewModel(plugin));
            }
            else
            {
                Panels.First(panel => panel.Id == plugin.Id).UpdateDiscovery(plugin);
            }
        }

        if (SelectedPanel is not null && !Panels.Contains(SelectedPanel))
        {
            SelectedPanel = null;
        }

        Summary = $"{Panels.Count} plugins installed";
        ConfigurePanels();
        RefreshReportingPanels();
        RefreshPsuComparisons(DateTimeOffset.UtcNow);
    }

    public void SetConfiguration(ConfigurationSnapshot configuration)
    {
        _configuration = configuration;
        ConfigurePanels();
        RefreshReportingPanels();
        RefreshPsuComparisons(DateTimeOffset.UtcNow);
    }

    public void Apply(SensorSnapshot snapshot, AppPreferences preferences, DateTimeOffset now, bool updateDetails = true,
        string? detailPanelId = null)
    {
        if (_configuration is { } configured)
        {
            var options = configured.Configuration.Plugins.GetValueOrDefault("asus-astral")?.Options?.Deserialize(ClientJsonContext.Default.AsusOptions)
                ?? new RTXLens.Plugins.Asus.Contracts.Configuration.AsusOptions();
            preferences = preferences with { CoreTemperatureWarningC = options.EffectiveCoreTemperatureWarningC,
                MemoryTemperatureWarningC = options.EffectiveMemoryTemperatureWarningC };
        }
        var states = snapshot.Warnings?.States ?? [];
        for (var index = CurrentWarnings.Count - 1; index >= states.Length; index--)
        {
            CurrentWarnings.RemoveAt(index);
        }

        for (var index = 0; index < states.Length; index++)
        {
            if (index >= CurrentWarnings.Count)
            {
                CurrentWarnings.Add(states[index]);
            }
            else if (CurrentWarnings[index] != states[index])
            {
                CurrentWarnings[index] = states[index];
            }
        }
        WarningSummary = snapshot.Warnings is null ? "Update the backend service to enable shared warning policies."
            : states.Length == 0 ? "No active warnings" : $"{states.Length} warning conditions - shared service state";
        Raise(nameof(WarningSummary));
        if (updateDetails && detailPanelId != "msi-psu")
        {
            GpuStatus.Apply(snapshot, _configuration, preferences, now);
        }
        _lastSnapshot = snapshot;
        _preferences = preferences;
        foreach (var panel in Panels)
        {
            panel.Apply(snapshot, preferences, now, updateDetails && (detailPanelId is null || detailPanelId == panel.Id));
        }

        RefreshReportingPanels();
        if (updateDetails && detailPanelId != "msi-psu")
        {
            RefreshPsuComparisons(now);
        }
    }

    public void RefreshFreshness(DateTimeOffset now, AppPreferences preferences)
    {
        GpuStatus.MarkExpired(now);
        _preferences = preferences;
        foreach (var panel in Panels)
        {
            panel.MarkExpired(now, preferences);
        }
        RefreshReportingPanels();
        RefreshPsuComparisons(now);
    }

    public void Disconnect(AppPreferences preferences)
    {
        GpuStatus.Clear();
        CurrentWarnings.Clear();
        WarningSummary = "Service unavailable - warning recovery cannot be evaluated.";
        Raise(nameof(WarningSummary));
        _lastSnapshot = null;
        _preferences = preferences;
        foreach (var panel in Panels)
        {
            panel.MarkDisconnected(preferences);
        }

        RefreshReportingPanels();
        RefreshPsuComparisons(DateTimeOffset.UtcNow);
    }

    private void RefreshPsuComparisons(DateTimeOffset now)
    {
        var source = Panels.FirstOrDefault(panel => panel.IsMsi);
        var staleAfterMs = _configuration?.Configuration.Plugins.GetValueOrDefault("msi-psu")?.StaleAfterMs ?? 5000;
        var readings = source is { Enabled: true, IsReporting: true, State: "Ready" } && _lastSnapshot is { } snapshot
            ? snapshot.Sensors.Where(reading => reading is { Identity: { PluginId: "msi-psu", Component: SensorComponent.PsuPin, Metric: SensorMetric.Current }, Unit: SensorUnit.Amperes, Quality: ReadingQuality.Valid, Value: { } value and >= 0 }
                                                && double.IsFinite(value) && now - reading.ObservedAt <= TimeSpan.FromMilliseconds(staleAfterMs)).ToArray()
            : [];
        if (readings.Select(reading => reading.Identity.DeviceId).Distinct().Skip(1).Any())
        {
            readings = [];
        }

        foreach (var panel in Panels.Where(panel => panel.IsWireView || panel.IsAsus))
        {
            panel.UpdatePsuComparison(readings, _preferences);
        }
    }

    private void RefreshReportingPanels()
    {
        var available = Panels.Where(panel => panel is { Enabled: true, IsReporting: true }).ToArray();
        HasAlternativeGpuStatus = available.Any(panel => panel.IsWireView || panel.IsAsus);
        var reporting = available.Where(panel => !panel.IsNvidia || _preferences.ShowSeparateGpuStatusTab || !HasAlternativeGpuStatus)
            .OrderBy(panel => panel.Id switch
            {
                "wireview-pro2" => 0,
                "asus-astral" => 1,
                "msi-psu" => 2,
                "generic-nvidia" => 3,
                _ => 4
            })
            .ThenBy(panel => panel.Id, StringComparer.Ordinal).ToArray();
        if (SelectedPanel is not null && !reporting.Contains(SelectedPanel))
        {
            SelectedPanel = null;
        }

        foreach (var panel in ReportingPanels.Except(reporting).ToArray())
        {
            ReportingPanels.Remove(panel);
        }

        for (var index = 0; index < reporting.Length; index++)
        {
            var currentIndex = ReportingPanels.IndexOf(reporting[index]);
            if (currentIndex < 0)
            {
                ReportingPanels.Insert(index, reporting[index]);
            }
            else if (currentIndex != index)
            {
                ReportingPanels.Move(currentIndex, index);
            }
        }
        Summary = $"{Panels.Count} plugins installed · {available.Length} reporting";
        PluginWarning = string.Join(Environment.NewLine, Panels.Where(panel => panel.Enabled && panel.State != "Disabled" && !panel.IsReporting)
            .Select(panel => $"{panel.Name}: {panel.State} · {panel.Message}"));
    }

    private void ConfigurePanels()
    {
        if (_configuration is null)
        {
            return;
        }

        foreach (var panel in Panels)
        {
            if (!_configuration.Configuration.Plugins.TryGetValue(panel.Id, out var settings))
            {
                continue;
            }

            var options = panel.IsAsus && settings.Options is { } json ? json.Deserialize(ClientJsonContext.Default.AsusOptions) : null;
            panel.Enabled = settings.Enabled;
            panel.Configure(settings.StaleAfterMs, panel.IsAsus ? options?.WireOvercurrentAmperageThreshold ?? 9.2 : null);
        }
    }
}
