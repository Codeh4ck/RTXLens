using System.Collections.ObjectModel;
using RTXLens.Contracts.Monitoring;
using RTXLens.Presentation.Infrastructure;

namespace RTXLens.Presentation.Configuration;

public sealed class ConnectorMetricEditorViewModel : EditableObject
{
    public Contracts.Enums.SensorMetric Metric { get; }
    public string Label => Metric == Contracts.Enums.SensorMetric.Current ? "Total current" : Metric.ToString();
    public bool AllowFallback { get; set => Edit(ref field, value); }
    public ObservableCollection<ConnectorPluginChoice> Sources { get; }
    public ConnectorPluginChoice[] AvailableSources { get; }
    public ConnectorPluginChoice? SelectedSource { get; set => Set(ref field, value); }
    public ConnectorPluginChoice? Candidate { get; set => Set(ref field, value); }
    public string Resolution { get; set => Set(ref field, value); } = "Waiting for readings";

    public ConnectorMetricEditorViewModel(ConnectorMetricSelection selection, ConnectorPluginChoice[] choices)
    {
        Metric = selection.Metric;
        AllowFallback = selection.AllowFallback;
        AvailableSources = choices;
        Sources = [.. selection.PreferredPlugins.Select(id => choices.FirstOrDefault(choice => choice.Id == id) ?? new(id, id))];
        Sources.CollectionChanged += (_, _) => NotifyEdited();
    }

    public void AddSource()
    {
        if (Candidate is { } candidate && Sources.All(source => source.Id != candidate.Id))
        {
            Sources.Add(candidate);
            SelectedSource = candidate;
        }
    }

    public void RemoveSource()
    {
        if (SelectedSource is not null)
        {
            Sources.Remove(SelectedSource);
        }
    }

    public void MoveSource(int direction)
    {
        var index = SelectedSource is null ? -1 : Sources.IndexOf(SelectedSource);
        var destination = index + direction;
        if (index >= 0 && destination >= 0 && destination < Sources.Count)
        {
            Sources.Move(index, destination);
        }
    }

    public ConnectorMetricSelection Build() => new()
    {
        Metric = Metric,
        AllowFallback = AllowFallback,
        PreferredPlugins = [.. Sources.Select(source => source.Id)]
    };
}
