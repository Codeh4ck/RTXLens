using RTXLens.Plugins.GenericNvidia.Contracts.Devices;
using System.Collections.ObjectModel;
using RTXLens.Client.Http;
using RTXLens.Plugins.Asus.Contracts.Common;
using RTXLens.Plugins.Msi.Contracts.Devices;
using RTXLens.Contracts.Configuration;
using RTXLens.Plugins.Msi.Contracts.Configuration;
using RTXLens.Contracts.Discovery;
using RTXLens.Contracts.Monitoring;
using RTXLens.Contracts.Sensors;
using RTXLens.Presentation.Infrastructure;

namespace RTXLens.Presentation.Configuration;

public sealed class ConfigurationEditorViewModel(Func<IMonitoringApi> api, bool localOnly = false,
    Func<ServiceConfiguration, ServiceConfiguration, Task>? recordChange = null) : ObservableObject
{
    private ConfigurationSnapshot? _baseline;
    private PluginCatalog? _catalog;
    private double _streamInterval = 1000;
    private double _eventLogRetentionDays = MonitoringOptions.DefaultLogRetentionDays;
    private double _eventLogStorageMb = MonitoringOptions.DefaultLogStorageMb;
    private AsusDeviceInfo[] _devices = [];
    private NvidiaDeviceInfo[] _nvidiaDevices = [];
    private MsiDeviceInfo[] _msiDevices = [];
    private bool _loading;
    private ConfigurationSnapshot? _pendingConfiguration;

    public ConnectorProfilesEditorViewModel ConnectorProfiles { get; } = new();
    public WarningOptionsEditorViewModel Warnings { get; } = new();
    public ObservableCollection<PluginSettingsEditorViewModel> Plugins { get; } = [];
    public ObservableCollection<SelectionEditorViewModel> Selections { get; } = [];
    public ObservableCollection<SensorChoice> AvailableSensors { get; } = [];

    public GenericNvidiaSettingsEditorViewModel? NvidiaWarnings => Plugins.FirstOrDefault(plugin => plugin.IsNvidia)?.Nvidia;
    public bool HasNvidiaWarnings => NvidiaWarnings is not null;
    public bool CanEdit => !IsBusy && _baseline is not null;
    public string Title => localOnly ? "Local simulation configuration" : "Service configuration";

    public bool IsDirty
    {
        get;
        private set => Set(ref field, value);
    }

    public bool IsBusy
    {
        get;
        private set
        {
            if (Set(ref field, value))
            {
                Raise(nameof(CanEdit));
            }
        }
    }

    public bool HasExternalChanges
    {
        get;
        private set => Set(ref field, value);
    }

    public string Message
    {
        get;
        set => Set(ref field, value);
    } = "Waiting for service configuration";

    public string RevisionText => _baseline is null ? "Not loaded" : $"Revision {_baseline.Revision}";
    public double StreamIntervalMs { get => _streamInterval; set { if (Set(ref _streamInterval, value))
        {
            MarkDirty();
        }
    } }

    public double EventLogRetentionDays
    {
        get => _eventLogRetentionDays;
        set
        {
            if (Set(ref _eventLogRetentionDays, value))
            {
                MarkDirty();
            }
        }
    }

    public double EventLogStorageMb
    {
        get => _eventLogStorageMb;
        set
        {
            if (Set(ref _eventLogStorageMb, value))
            {
                MarkDirty();
            }
        }
    }

    public void SetCatalog(PluginCatalog catalog)
    {
        _catalog = catalog;
        Warnings.SetCatalogue(catalog);
    }

    public void Receive(ConfigurationSnapshot snapshot)
    {
        if (IsBusy)
        {
            return;
        }

        if (IsDirty)
        {
            _pendingConfiguration = snapshot;
            HasExternalChanges = snapshot.Version != _baseline?.Version;
            return;
        }

        Load(snapshot);
    }

    public void SetDevices(AsusDeviceInfo[] devices)
    {
        _devices = devices;
        foreach (var plugin in Plugins.Where(plugin => plugin.IsAsus))
        {
            plugin.Asus.SetDevices(devices);
        }
    }

    public void SetNvidiaDevices(NvidiaDeviceInfo[] devices)
    {
        _nvidiaDevices = devices;
        foreach (var plugin in Plugins.Where(plugin => plugin.IsNvidia))
        {
            plugin.Nvidia.SetDevices(devices);
        }
    }

    public void SetMsiDevices(MsiDeviceInfo[] devices)
    {
        _msiDevices = devices;
        foreach (var plugin in Plugins.Where(plugin => plugin.IsMsi))
        {
            plugin.Msi.SetDevices(devices);
        }
    }

    public void SetAvailableSensors(SensorSnapshot snapshot)
    {
        var identities = snapshot.Sensors.Select(reading => reading.Identity).OrderBy(identity => identity.Key, StringComparer.Ordinal).ToArray();
        if (!AvailableSensors.Select(sensor => sensor.Identity.Key).SequenceEqual(identities.Select(identity => identity.Key)))
        {
            AvailableSensors.Clear();
            foreach (var identity in identities)
            {
                AvailableSensors.Add(new SensorChoice(identity));
            }
        }

        ConnectorProfiles.Apply(snapshot);

        foreach (var selection in Selections)
        {
            var resolved = snapshot.Selections.FirstOrDefault(item => item.Name == selection.Name);
            var source = AvailableSensors.FirstOrDefault(item => item.Identity.Key == resolved?.SensorKey);
            selection.Resolution = resolved is null ? "Not applied" : $"{resolved.State} · {source?.Label ?? "No usable source"}";
        }
    }

    public void AddSelection()
    {
        var selection = new SelectionEditorViewModel(new SensorSelection { Name = "New selection" }, AvailableSensors);
        selection.Edited += MarkDirty;
        Selections.Add(selection);
        MarkDirty();
    }

    public void RemoveSelection(SelectionEditorViewModel selection)
    {
        selection.Edited -= MarkDirty;
        if (Selections.Remove(selection))
        {
            MarkDirty();
        }
    }

    public async Task ReloadAsync(CancellationToken token)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            Load(await api().GetConfigurationAsync(token));
        }
        catch (Exception exception) when (IsRequestError(exception))
        {
            Message = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ApplyAsync(CancellationToken token)
    {
        if (IsBusy || _baseline is null || !IsDirty)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var monitoring = new MonitoringOptions
            {
                ConnectorProfiles = ConnectorProfiles.Build(),
                StreamIntervalMs = NumericInput.Integer(StreamIntervalMs, "Stream interval"),
                EventLogRetentionDays = NumericInput.Integer(EventLogRetentionDays, "Event log retention"),
                EventLogStorageMb = NumericInput.Integer(EventLogStorageMb, "Maximum log storage"),
                Selections = [.. Selections.Select(selection => selection.Build())]
            };
            if (!monitoring.IsValid())
            {
                throw new ArgumentException("Invalid streaming settings or source selections. Check the configured limits and source priorities.");
            }

            var candidate = _baseline.Configuration with
            {
                Warnings = Warnings.Build(),
                Monitoring = monitoring,
                Plugins = Plugins.ToDictionary(plugin => plugin.Id, plugin => plugin.Build(), StringComparer.Ordinal)
            };
            var previous = _baseline.Configuration;
            var updated = await api().SetConfigurationAsync(candidate,
                _baseline.Version ?? throw new InvalidOperationException("The service must support versioned configuration writes."), token);
            if (recordChange is not null)
            {
                await recordChange(previous, updated.Configuration);
            }

            Load(updated);
            Message = localOnly ? $"Applied to local simulation · revision {updated.Revision}" : $"Applied live · revision {updated.Revision}";
        }
        catch (MonitoringApiException exception) when (exception.IsConflict)
        {
            HasExternalChanges = true;
            Message = exception.Message;
        }
        catch (Exception exception) when (IsRequestError(exception) || exception is ArgumentException or InvalidOperationException)
        {
            Message = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void ReceiveMsiCurveUpdate(ConfigurationSnapshot snapshot, MsiFanCurve curve, string previousVersion)
    {
        if (_baseline?.Version != previousVersion)
        {
            Receive(snapshot);
            return;
        }

        _loading = true;

        try
        {
            _baseline = snapshot;
            _pendingConfiguration = null;
            HasExternalChanges = false;
            var msi = Plugins.Single(plugin => plugin.IsMsi).Msi;
            msi.SetCurve(curve);
            msi.FanModeIndex = (int)MsiFanControl.Curve;
            Raise(nameof(RevisionText));
        }
        finally
        {
            _loading = false;
        }

        MarkDirty();
    }

    private void Load(ConfigurationSnapshot snapshot)
    {
        _loading = true;
        try
        {
            foreach (var plugin in Plugins)
            {
                plugin.Edited -= MarkDirty;
            }

            foreach (var selection in Selections)
            {
                selection.Edited -= MarkDirty;
            }

            Plugins.Clear();
            Selections.Clear();
            _baseline = snapshot;
            ConnectorProfiles.Edited -= MarkDirty;
            ConnectorProfiles.Load(snapshot.Configuration.Monitoring.ConnectorProfiles, _catalog);
            ConnectorProfiles.Edited += MarkDirty;
            Warnings.Edited -= MarkDirty;
            Warnings.Load(snapshot.Configuration.Warnings);
            Warnings.Edited += MarkDirty;
            _streamInterval = snapshot.Configuration.Monitoring.StreamIntervalMs;
            _eventLogRetentionDays = snapshot.Configuration.Monitoring.EventLogRetentionDays;
            _eventLogStorageMb = snapshot.Configuration.Monitoring.EventLogStorageMb;
            foreach (var (id, settings) in snapshot.Configuration.Plugins)
            {
                var name = _catalog?.Plugins.FirstOrDefault(plugin => plugin.Id == id)?.Name ?? id;
                var plugin = new PluginSettingsEditorViewModel(id, name, settings);
                plugin.Asus.SetDevices(_devices);
                plugin.Nvidia.SetDevices(_nvidiaDevices);
                plugin.Msi.SetDevices(_msiDevices);
                plugin.Edited += MarkDirty;
                Plugins.Add(plugin);
            }

            foreach (var settings in snapshot.Configuration.Monitoring.Selections)
            {
                var selection = new SelectionEditorViewModel(settings, AvailableSensors);
                selection.Edited += MarkDirty;
                Selections.Add(selection);
            }

            IsDirty = false;
            _pendingConfiguration = null;
            HasExternalChanges = false;
            Message = snapshot.LastError ?? (localOnly ? "Changes apply only to this local simulation. The service and other consumers are unchanged."
                : "Changes are applied live to the service and all consumers.");
            Raise(nameof(StreamIntervalMs));
            Raise(nameof(EventLogRetentionDays));
            Raise(nameof(EventLogStorageMb));
            Raise(nameof(RevisionText));
            Raise(nameof(NvidiaWarnings));
            Raise(nameof(HasNvidiaWarnings));
            Raise(nameof(CanEdit));
        }
        finally
        {
            _loading = false;
        }
    }

    private void MarkDirty()
    {
        if (_loading || _baseline is null)
        {
            return;
        }

        var original = _baseline.Configuration;
        IsDirty = !ConnectorProfiles.Matches(original.Monitoring.ConnectorProfiles) || !Warnings.Matches(original.Warnings) || !double.IsFinite(StreamIntervalMs) || Math.Abs(StreamIntervalMs - original.Monitoring.StreamIntervalMs) > 0.001
            || EventLogRetentionDays != original.Monitoring.EventLogRetentionDays || EventLogStorageMb != original.Monitoring.EventLogStorageMb
            || Plugins.Count != original.Plugins.Count
            || Plugins.Any(plugin => !original.Plugins.TryGetValue(plugin.Id, out var settings) || !plugin.Matches(settings))
            || Selections.Count != original.Monitoring.Selections.Count
            || Selections.Where((selection, index) => !selection.Matches(original.Monitoring.Selections[index])).Any();

        if (!IsDirty && _pendingConfiguration is { } pending)
        {
            Load(pending);
        }
    }
    private static bool IsRequestError(Exception exception) => exception is HttpRequestException or MonitoringApiException or IOException
        or OperationCanceledException or System.Text.Json.JsonException;
}
