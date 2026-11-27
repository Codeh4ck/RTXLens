using RTXLens.Contracts.Enums;
using RTXLens.Debugging.Catalog;
using RTXLens.Debugging.Models;
using RTXLens.Presentation.Infrastructure;

namespace RTXLens.Debugging.ViewModels;

public sealed class DebugPluginViewModel : ObservableObject
{
    private DebugSourceMode _mode;
    private bool _installed = true;
    private bool _enabled = true;
    private PluginState _state = PluginState.Ready;
    private string _deviceId;
    public string Id { get; }
    public string Name { get; }
    public DebugMetricViewModel[] Metrics { get; }
    public event Action<bool>? Edited;
    public DebugSourceMode Mode => _mode;

    public int ModeIndex
    {
        get => (int)_mode;
        set
        {
            if (value is < 0 or > 2 || !Set(ref _mode, (DebugSourceMode)value))
            {
                return;
            }

            Raise(nameof(Mode));
            Raise(nameof(IsSimulated));

            Edited?.Invoke(true);
        }
    }

    public bool IsSimulated => Mode == DebugSourceMode.Simulated;

    public bool Installed
    {
        get => _installed;
        set
        {
            if (Set(ref _installed, value))
            {
                Edited?.Invoke(true);
            }
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (Set(ref _enabled, value))
            {
                Edited?.Invoke(true);
            }
        }
    }

    public int StateIndex
    {
        get => (int)_state;
        set
        {
            if (Enum.IsDefined((PluginState)value) && Set(ref _state, (PluginState)value))
            {
                Edited?.Invoke(true);
            }
        }
    }

    public PluginState State => _state;

    public string DeviceId
    {
        get => _deviceId;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && value.Length <= 128 && Set(ref _deviceId, value))
            {
                Edited?.Invoke(true);
            }
        }
    }

    public DebugPluginViewModel(string id, string name)
    {
        Id = id;
        Name = name;
        _deviceId = "debug-" + id;

        Metrics = [.. DebugMetricCatalog.For(id).Select(definition => new DebugMetricViewModel(definition))];

        foreach (var metric in Metrics)
        {
            metric.Edited += () => Edited?.Invoke(false);
        }
    }

    public void Reset()
    {
        _mode = DebugSourceMode.Live;
        _installed = _enabled = true;
        _state = PluginState.Ready;
        _deviceId = "debug-" + Id;

        foreach (var metric in Metrics)
        {
            metric.Reset();
        }

        Raise(string.Empty);
        Edited?.Invoke(true);
    }
}