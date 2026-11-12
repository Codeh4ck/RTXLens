using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using RTXLens.App.ViewModels.Shell;
using RTXLens.Client.Http;
using RTXLens.Contracts.Configuration;
using RTXLens.Presentation.Configuration;
using static RTXLens.App.Views.Configuration.ConfigurationPage;
using static RTXLens.App.Views.Configuration.WarningsConfigurationView;
using static RTXLens.App.Views.Shell.DesktopControls;

namespace RTXLens.App.Views.Configuration;

internal sealed class DeviceWarningProfilesView : ProfileRequestView
{
    private static readonly (string Id, string Label)[] Sources =
    [
        ("asus-astral", "ROG Astral"), ("msi-psu", "MSI PSU"), ("wireview-pro2", "WireView Pro II")
    ];

    private readonly ComboBox _profiles = new()
    {
        Width = 220,
        PlaceholderText = "Select a profile",
        VerticalAlignment = VerticalAlignment.Center,
        ItemTemplate = new FuncDataTemplate<ProfileOption>((item, _) => new TextBlock
        {
            Text = item?.ToString(),
            TextTrimming = TextTrimming.CharacterEllipsis
        })
    };

    private readonly ToggleSwitch _enabled = Switch("Enabled");
    private readonly ComboBox _source = new() { ItemsSource = Sources.Select(source => source.Label).ToArray() };
    private readonly ComboBox _device = new();
    private readonly ComboBox _connector = new() { ItemsSource = new[] { "Connector 1", "Connector 2" } };
    private readonly ToggleSwitch _useDefaults = Switch("Use detected GPU defaults");
    private readonly NumericUpDown _overcurrent = Number(6, 10.5m, 0.1m);
    private readonly NumericUpDown _overcurrentDuration = Number(100, 30000, 1);
    private readonly NumericUpDown _activation = Number(1, 10, 1);
    private readonly NumericUpDown _imbalance = Number(10, 50, 1);
    private readonly NumericUpDown _imbalanceDuration = Number(100, 30000, 1);
    private readonly NumericUpDown _low = Number(5, 11.99m, 0.1m);
    private readonly NumericUpDown _high = Number(12.01m, 20, 0.1m);
    private readonly WarningPolicyEditorViewModel _policy = new();
    private readonly ToggleSwitch _overridePolicy = Switch("Customize notifications for this profile");
    private readonly ToggleSwitch _shutdown = Switch("Shut down Windows if a warning persists");
    private readonly NumericUpDown _minutes = Number(0, 1439, 1);
    private readonly NumericUpDown _seconds = Number(0, 59, 1);
    private readonly Button _add;
    private readonly Button _delete;
    private readonly StackPanel _editor;
    private readonly DockPanel _deviceFields;
    private readonly Grid _sourceFields;
    private readonly Button _save;
    private readonly StackPanel _deviceField;
    private readonly StackPanel _connectorField;
    private readonly Grid _voltageFields;
    private readonly StackPanel _policyFields;
    private readonly Grid _shutdownFields;
    private DeviceWarningProfileCatalog? _catalog;
    private DeviceWarningProfile? _draft;
    private WarningProfileTarget[] _devices = [];
    private bool _loading;

    public DeviceWarningProfilesView(ShellViewModel shell) : base(shell)
    {
        AutomationProperties.SetName(_profiles, "Warning profile");
        AutomationProperties.SetName(_enabled, "Profile enabled");
        _enabled.Margin = new Thickness(0, 0, 12, 0);
        _enabled.VerticalAlignment = VerticalAlignment.Center;

        _add = IconButton("Add warning profile", "add-action", async void () =>
        {
            if (TopLevel.GetTopLevel(this) is Window owner &&
                await RequestProfileNameAsync(owner, "New warning profile") is { } name)
            {
                var draft = new DeviceWarningProfile { Id = Guid.NewGuid().ToString("N"), Name = name };
                _profiles.ItemsSource = _catalog!.Profiles.Append(draft).Select(profile => new ProfileOption(profile)).ToArray();
                _profiles.SelectedIndex = _profiles.ItemCount - 1;
                Report(null);
            }
        });

        _delete = IconButton("Delete warning profile", "chart-clear", async void () =>
        {
            if (_draft is { } profile && TopLevel.GetTopLevel(this) is Window owner
                && await ConfirmAsync(owner, "Delete warning profile", $"Delete {profile.Name}?", "Delete"))
            {
                if (_catalog?.Profiles.Any(saved => saved.Id == profile.Id) == true)
                {
                    await RunAsync(api => DeleteAsync(api, profile));
                }
                else
                {
                    LoadCatalog(_catalog!);
                }
            }
        });

        _save = CommitButton("Save profile configuration", async void () => await RunAsync(SaveAsync));
        _save.HorizontalAlignment = HorizontalAlignment.Right;
        _save.VerticalAlignment = VerticalAlignment.Bottom;

        DockPanel.SetDock(_save, Dock.Right);

        _deviceField = Field("Device", _device);
        _connectorField = Field("PSU connector", _connector);
        _voltageFields = Columns(Field("Low pin voltage (V)", _low), Field("High pin voltage (V)", _high));
        _policyFields = PolicyFields(_policy);
        _shutdownFields = Columns(Field("Minutes", _minutes), Field("Seconds", _seconds));

        var thresholds = Section("Warning thresholds", Stack(_useDefaults,
            Columns(Field("Per-pin overcurrent (A)", _overcurrent), Field("Overcurrent duration (ms)", _overcurrentDuration)),
            Columns(Field("Highest pin to check imbalance (A)", _activation), Field("Current imbalance (%)", _imbalance),
                Field("Imbalance duration (ms)", _imbalanceDuration)),
            _voltageFields));

        var advanced = Section("Notifications and shutdown", Stack(_overridePolicy, _policyFields, _shutdown, _shutdownFields));

        var info = IconButton("How warning profiles work", "info-action", async void () =>
        {
            if (TopLevel.GetTopLevel(this) is Window owner)
            {
                var defaults = _catalog is { } catalog ? $"the RTX {catalog.GpuModel} preset" : "the preset for the detected GPU";

                await ShowInformationAsync(owner, "Warning profiles", UsageGuide(
                    "Each enabled profile monitors the device or PSU connector you choose. WireView Pro II firmware alerts still work independently.",
                    "Add a profile, give it a name, and select a reading source. Select the PSU connector that feeds the GPU "
                        + "when using MSI PSU telemetry.",
                    $"Keep detected GPU defaults to use {defaults}, or turn them off to set your own limits. "
                        + "Current warnings require the configured duration to elapse. "
                        + "The imbalance check also requires at least one pin to reach the activation current.",
                    "Use the Notifications and shutdown panel to override notification timing for this profile. "
                        + "You can also request a Windows shutdown after a warning persists for the delay you set. "
                        + "Recovery or unavailable readings reset the shutdown countdown.",
                    "Turn on Enabled and select Save profile configuration to save the profile shown in the selector."));
            }
        });

        _sourceFields = Columns(Field("Reading source", _source), _deviceField, _connectorField);
        _deviceFields = new DockPanel { Children = { _save, _sourceFields } };

        _deviceFields.SizeChanged += (_, args) =>
        {
            var compact = args.NewSize.Width < 720;
            DockPanel.SetDock(_save, compact ? Dock.Bottom : Dock.Right);
            _save.Margin = compact ? new Thickness(0, 10, 0, 0) : new Thickness(12, 0, 0, 0);
        };

        var deviceBody = Stack(_deviceFields, Status);
        var panel = Section("Device warning profiles", deviceBody);

        var profileActions = new WrapPanel
        {
            ItemSpacing = 8,
            LineSpacing = 8,
            Children = { _enabled, _profiles, _add, _delete, info }
        };

        panel.HeaderActions = profileActions;
        panel.SizeChanged += (_, args) =>
        {
            var useHeader = args.NewSize.Width >= 640;

            switch (useHeader)
            {
                case true when panel.HeaderActions is null:
                    deviceBody.Children.Remove(profileActions);
                    panel.HeaderActions = profileActions;
                    break;
                case false when panel.HeaderActions is not null:
                    panel.HeaderActions = null;
                    deviceBody.Children.Insert(0, profileActions);
                    break;
            }
        };

        _editor = Stack(thresholds, advanced);
        Content = Stack(panel, _editor);

        _profiles.SelectionChanged += (_, _) =>
        {
            if (!_loading && _profiles.SelectedItem is ProfileOption selected)
            {
                ShowDraft(selected.Profile);
                Report(null);
            }
        };

        _source.SelectionChanged += (_, _) =>
        {
            if (!_loading && _source.SelectedIndex >= 0)
            {
                PopulateDevices(null);
                if (_useDefaults.IsChecked == true)
                {
                    SetLimits(DefaultLimits());
                }

                UpdateAvailability();
            }
        };

        _useDefaults.IsCheckedChanged += (_, _) =>
        {
            if (!_loading)
            {
                if (_useDefaults.IsChecked == true)
                {
                    SetLimits(DefaultLimits());
                }
            }

            UpdateAvailability();
        };

        _overridePolicy.IsCheckedChanged += (_, _) => UpdateAvailability();
        _shutdown.IsCheckedChanged += (_, _) => UpdateAvailability();
        _enabled.IsCheckedChanged += (_, _) => UpdateSaveState();
        _device.SelectionChanged += (_, _) => UpdateSaveState();
        _connector.SelectionChanged += (_, _) => UpdateSaveState();
        _policy.Edited += UpdateSaveState;

        foreach (var input in new[] { _overcurrent, _overcurrentDuration, _activation, _imbalance, _imbalanceDuration, _low, _high, _minutes, _seconds })
        {
            input.ValueChanged += (_, _) => UpdateSaveState();
        }

        UpdateAvailability();

        AttachedToVisualTree += async (_, _) =>
        {
            if (_catalog is null)
            {
                await RunAsync(api => LoadAsync(api));
            }
        };
    }

    private string? PluginId => _source.SelectedIndex < 0 ? null : Sources[_source.SelectedIndex].Id;

    protected override void UpdateAvailability()
    {
        _add.IsEnabled = _profiles.IsEnabled = !IsBusy && _catalog is not null;
        _editor.IsEnabled = _deviceFields.IsEnabled = _enabled.IsEnabled = !IsBusy && _draft is not null;
        _delete.IsEnabled = !IsBusy && _draft is not null;

        var msi = PluginId == "msi-psu";
        _connectorField.IsVisible = msi;
        _voltageFields.IsVisible = !msi;
        _sourceFields.ColumnDefinitions[1].Width = msi ? new GridLength(1.25, GridUnitType.Star) : GridLength.Star;
        _sourceFields.ColumnDefinitions[2].Width = msi ? new GridLength(0.8, GridUnitType.Star) : new GridLength(0);

        Grid.SetColumnSpan(_deviceField, msi ? 1 : 2);

        foreach (var input in new[] { _overcurrent, _overcurrentDuration, _activation, _imbalance, _imbalanceDuration, _low, _high })
        {
            input.IsEnabled = _useDefaults.IsChecked != true;
        }

        _policyFields.IsEnabled = _overridePolicy.IsChecked == true;
        _shutdownFields.IsEnabled = _shutdown.IsChecked == true;

        UpdateSaveState();
    }

    private void UpdateSaveState()
    {
        if (_loading)
        {
            return;
        }

        var hasChanges = false;
        if (_draft is { } draft && PluginId is { } pluginId)
        {
            var saved = _catalog?.Profiles.FirstOrDefault(profile => profile.Id == draft.Id);
            try
            {
                hasChanges = saved is null || BuildProfile(draft, pluginId) != saved;
            }
            catch (ArgumentException)
            {
                hasChanges = true;
            }
        }

        _save.IsEnabled = hasChanges && !IsBusy;
        _save.Classes.Set("unsaved-profile", _save.IsEnabled);
    }

    private async Task LoadAsync(IMonitoringApi api, string? selectedId = null)
    {
        var catalog = await api.GetDeviceWarningProfilesAsync(Shell.StopToken);
        LoadCatalog(catalog, selectedId);
    }

    private void LoadCatalog(DeviceWarningProfileCatalog catalog, string? selectedId = null)
    {
        var options = catalog.Profiles.Select(profile => new ProfileOption(profile)).ToArray();
        var selected = options.FirstOrDefault(option => option.Profile.Id == selectedId) ?? options.FirstOrDefault();

        _catalog = catalog;
        _loading = true;
        _profiles.ItemsSource = options;
        _profiles.SelectedItem = selected;
        _loading = false;
        
        ShowDraft(selected?.Profile);
        Report(null);
    }

    private void ShowDraft(DeviceWarningProfile? profile)
    {
        _draft = profile;
        if (profile is not null)
        {
            _loading = true;
            try
            {
                _enabled.IsChecked = profile.Enabled;
                _source.SelectedIndex = Array.FindIndex(Sources, source => source.Id == profile.PluginId);
                PopulateDevices(profile.DeviceId);
                _connector.SelectedIndex = profile.ConnectorId == "2" ? 1 : 0;
                _useDefaults.IsChecked = profile.Limits is null;
                SetLimits(profile.Limits ?? DefaultLimits());
                _overridePolicy.IsChecked = profile.NotificationPolicy is not null;
                _policy.Load(profile.NotificationPolicy ?? _catalog!.DefaultPolicy);
                _shutdown.IsChecked = profile.ShutdownAfterSeconds is not null;
                var seconds = profile.ShutdownAfterSeconds ?? 30;
                _minutes.Value = seconds / 60;
                _seconds.Value = seconds % 60;
            }
            finally
            {
                _loading = false;
            }
        }

        UpdateAvailability();
    }

    private void PopulateDevices(string? selectedId)
    {
        var pluginId = PluginId;
        var devices = _catalog!.Targets.Where(target => target.PluginId == pluginId).ToList();
        if (selectedId is not null && devices.All(target => target.DeviceId != selectedId))
        {
            devices.Add(new WarningProfileTarget(pluginId ?? "", selectedId, $"{selectedId} (unavailable)"));
        }

        devices.Insert(0, new WarningProfileTarget(pluginId ?? "", "", "Automatic (one reporting device)"));
        _devices = [.. devices];
        _device.ItemsSource = _devices.Select(target => target.Name).ToArray();
        _device.SelectedIndex = Math.Max(0, Array.FindIndex(_devices, target => target.DeviceId == selectedId));
    }

    private ConnectorWarningLimits DefaultLimits() => PluginId == "asus-astral" ? _catalog!.AstralDefaults : _catalog!.Defaults;

    private void SetLimits(ConnectorWarningLimits limits)
    {
        _overcurrent.Value = (decimal)limits.OvercurrentAmperes;
        _overcurrentDuration.Value = limits.OvercurrentMilliseconds;
        _activation.Value = limits.ImbalanceActivationAmperes;
        _imbalance.Value = limits.ImbalancePercentage;
        _imbalanceDuration.Value = limits.ImbalanceMilliseconds;
        _low.Value = (decimal)limits.LowPinVoltage;
        _high.Value = (decimal)limits.HighPinVoltage;
    }

    private DeviceWarningProfile BuildProfile(DeviceWarningProfile draft, string pluginId)
    {
        var device = _device.SelectedIndex < 0 ? null : _devices[_device.SelectedIndex].DeviceId;
        var limits = _useDefaults.IsChecked == true ? null : new ConnectorWarningLimits(ReadNumber(_overcurrent, "per-pin overcurrent"),
            Whole(_overcurrentDuration, "Overcurrent duration"), Whole(_activation, "Imbalance activation"),
            Whole(_imbalance, "Imbalance percentage"), Whole(_imbalanceDuration, "Imbalance duration"),
            pluginId == "msi-psu" ? draft.Limits?.LowPinVoltage ?? DefaultLimits().LowPinVoltage : ReadNumber(_low, "low pin voltage"),
            pluginId == "msi-psu" ? draft.Limits?.HighPinVoltage ?? DefaultLimits().HighPinVoltage : ReadNumber(_high, "high pin voltage"));
        return draft with
        {
            PluginId = pluginId,
            DeviceId = string.IsNullOrEmpty(device) ? null : device,
            ConnectorId = pluginId == "msi-psu" ? (_connector.SelectedIndex == 1 ? "2" : "1") : null,
            Enabled = _enabled.IsChecked == true,
            Limits = limits,
            NotificationPolicy = _overridePolicy.IsChecked == true ? _policy.Build() : null,
            ShutdownAfterSeconds = _shutdown.IsChecked == true ? Whole(_minutes, "Minutes") * 60 + Whole(_seconds, "Seconds") : null
        };
    }

    private async Task SaveAsync(IMonitoringApi api)
    {
        if (_draft is not { } draft || PluginId is not { } pluginId)
        {
            return;
        }

        var profile = BuildProfile(draft, pluginId);
        if (!profile.IsValid())
        {
            throw new ArgumentException("Enter valid thresholds. A shutdown delay must be between 00:01 and 1439:59.");
        }

        var previous = _catalog?.Profiles.FirstOrDefault(item => item.Id == profile.Id);
        await api.SaveDeviceWarningProfileAsync(profile, Shell.StopToken);
        await Shell.LocalEvents.RecordAsync($"Warning profile ({profile.Name})", previous, profile);
        await LoadAsync(api, profile.Id);
        Report($"Saved {profile.Name}.", "success");
    }

    private async Task DeleteAsync(IMonitoringApi api, DeviceWarningProfile profile)
    {
        await api.DeleteDeviceWarningProfileAsync(profile.Id, Shell.StopToken);
        await Shell.LocalEvents.RecordAsync<DeviceWarningProfile?>($"Warning profile ({profile.Name})", profile, null);
        await LoadAsync(api);
        Report($"Deleted {profile.Name}.", "success");
    }

    private sealed record ProfileOption(DeviceWarningProfile Profile)
    {
        public override string ToString() => Profile.Enabled ? Profile.Name : $"{Profile.Name} (disabled)";
    }
}
