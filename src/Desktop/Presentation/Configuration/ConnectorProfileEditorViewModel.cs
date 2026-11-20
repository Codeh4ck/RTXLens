using RTXLens.Plugins.Msi.Contracts;
using RTXLens.Plugins.WireView.Contracts;
using RTXLens.Contracts.Monitoring;
using RTXLens.Presentation.Infrastructure;

namespace RTXLens.Presentation.Configuration;

public sealed class ConnectorProfileEditorViewModel : EditableObject
{
    private readonly Dictionary<string, string> _connectors;
    public string Id { get; }
    public string Name { get; }
    public string DisplayName => Id == WireViewIdentity.Id && Name == "WireView" ? "WireView Pro II" : Name;
    public ConnectorMetricEditorViewModel[] Metrics { get; }
    public bool HasMsi { get; }
    public int MsiConnectorIndex { get; set => Edit(ref field, value); }

    public ConnectorProfileEditorViewModel(ConnectorSourceProfile profile, ConnectorPluginChoice[] choices, Contracts.Discovery.PluginCatalog? catalog = null)
    {
        Id = profile.Id;
        Name = profile.Name;
        HasMsi = choices.Any(choice => choice.Id == MsiIdentity.Id);
        _connectors = new(profile.Connectors, StringComparer.Ordinal);
        MsiConnectorIndex = _connectors.GetValueOrDefault(MsiIdentity.Id) switch { "1" => 1, "2" => 2, _ => 0 };
        Metrics = [.. profile.Metrics.Select(metric => new ConnectorMetricEditorViewModel(metric,
            [.. choices.Where(choice => catalog?.Plugins.FirstOrDefault(plugin => plugin.Id == choice.Id)?.ConnectorMetrics is { Length: > 0 } capabilities
                ? capabilities.Contains(metric.Metric) : metric.Metric == Contracts.Enums.SensorMetric.Current || choice.Id != MsiIdentity.Id)]))];

        foreach (var metric in Metrics)
        {
            metric.Edited += NotifyEdited;
        }
    }

    public ConnectorSourceProfile Build()
    {
        var connectors = new Dictionary<string, string>(_connectors, StringComparer.Ordinal);
        if (MsiConnectorIndex is 1 or 2)
        {
            connectors[MsiIdentity.Id] = MsiConnectorIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        else
        {
            connectors.Remove(MsiIdentity.Id);
        }

        return new ConnectorSourceProfile { Id = Id, Name = Name, Metrics = [.. Metrics.Select(metric => metric.Build())], Connectors = connectors };
    }
}
