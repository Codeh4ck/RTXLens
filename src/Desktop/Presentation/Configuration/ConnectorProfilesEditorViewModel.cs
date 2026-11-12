using System.Collections.ObjectModel;
using System.Text.Json;
using RTXLens.Contracts.Discovery;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Monitoring;
using RTXLens.Contracts.Sensors;
using RTXLens.Presentation.Infrastructure;

namespace RTXLens.Presentation.Configuration;

public sealed class ConnectorProfilesEditorViewModel : EditableObject
{
    private bool _loading;
    private string? _appliedProfileId;
    public ObservableCollection<ConnectorProfileEditorViewModel> Profiles { get; } = [];
    public ConnectorProfileEditorViewModel? SelectedProfile
    {
        get;
        set
        {
            if (value is not null && Set(ref field, value) && !_loading)
            {
                NotifyEdited();
            }
        }
    }

    public void Load(ConnectorProfiles options, PluginCatalog? catalog)
    {
        _loading = true;
        try
        {
            _appliedProfileId = options.ActiveProfileId;
            Profiles.Clear();
            var ids = options.Profiles.SelectMany(profile => profile.Metrics).SelectMany(metric => metric.PreferredPlugins)
                .Concat(catalog?.Plugins.Where(plugin => plugin.ConnectorMetrics.Length > 0).Select(plugin => plugin.Id) ?? [])
                .Distinct(StringComparer.Ordinal).ToArray();
            var choices = ids.Select(id => new ConnectorPluginChoice(id,
                id switch
                {
                    "generic-nvidia" => "Nvidia",
                    "wireview-pro2" => "WireView Pro II",
                    "asus-astral" => "ROG Astral",
                    "msi-psu" => "MSI PSU",
                    _ => catalog?.Plugins.FirstOrDefault(plugin => plugin.Id == id)?.Name ?? id
                })).ToArray();

            foreach (var profile in options.Profiles)
            {
                var editor = new ConnectorProfileEditorViewModel(profile, choices, catalog);
                editor.Edited += NotifyEdited;
                Profiles.Add(editor);
            }

            SelectedProfile = Profiles.FirstOrDefault(profile => profile.Id == options.ActiveProfileId);
        }
        finally
        {
            _loading = false;
        }
    }

    public ConnectorProfiles Build() => new()
    {
        Version = Profiles.Count == 0 ? 0 : 1,
        ActiveProfileId = SelectedProfile?.Id ?? "automatic",
        Profiles = [.. Profiles.Select(profile => profile.Build())]
    };

    public bool Matches(ConnectorProfiles options) => JsonSerializer.Serialize(Build()) == JsonSerializer.Serialize(options);

    public void Apply(SensorSnapshot snapshot)
    {
        foreach (var profile in Profiles)
        {
            foreach (var metric in profile.Metrics)
            {
                if (profile != SelectedProfile || profile.Id != _appliedProfileId)
                {
                    metric.Resolution = "Select this profile and apply to use it.";
                    continue;
                }

                var selectionName = ConnectorProfiles.SelectionName(metric.Metric);
                var resolved = snapshot.Selections.FirstOrDefault(selection => selection.Name == selectionName);
                var reading = resolved?.SensorKey is { } sensorKey
                    ? snapshot.Sensors.FirstOrDefault(reading => reading.Identity.Key == sensorKey)
                    : null;

                if (reading is null)
                {
                    metric.Resolution = "Unavailable - no source is reporting a usable reading";
                    continue;
                }

                var source = metric.AvailableSources.FirstOrDefault(choice => choice.Id == reading.Identity.PluginId)?.Label
                    ?? "a reporting plugin";
                if (reading.Identity is { Component: SensorComponent.PsuConnector, ConnectorId: "1" or "2" } identity)
                {
                    source = $"{source} · Connector {identity.ConnectorId}";
                }

                var fallback = resolved?.State == SelectionState.Fallback ? " (fallback)" : string.Empty;
                metric.Resolution = $"Using {source}{fallback}";
            }
        }
    }
}
