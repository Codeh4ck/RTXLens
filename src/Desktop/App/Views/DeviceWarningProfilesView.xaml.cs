using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RTXLens.App.Services;
using RTXLens.Client.Http;
using RTXLens.Contracts.Configuration;
using RTXLens.Presentation.Configuration;

namespace RTXLens.App.Views;

public sealed partial class DeviceWarningProfilesView
{
    private static readonly SourceOption[] Sources =
    [
        new("asus-astral", "ROG Astral"),
        new("msi-psu", "MSI PSU"),
        new("wireview-pro2", "WireView Pro 2")
    ];

    private readonly AppRuntime _runtime;
    private readonly WarningPolicyEditorViewModel _policy = new();
    private DeviceWarningProfileCatalog? _catalog;
    private DeviceWarningProfile? _draft;
    private WarningProfileTarget[] _devices = [];
    private bool _loading;
    private bool _busy;

    internal DeviceWarningProfilesView(AppRuntime runtime)
    {
        _runtime = runtime;
        InitializeComponent();

        Source.ItemsSource = Sources.Select(source => source.Label).ToArray();
        PolicyHost.Content = new WarningPolicyView(_policy);
        Loaded += async (_, _) =>
        {
            if (_catalog is null)
            {
                await RunAsync(() => LoadAsync());
            }
        };
    }

    private async void Refresh_Click(object sender, RoutedEventArgs args) =>
        await RunAsync(() => LoadAsync(_draft?.Id));

    private void Add_Click(object sender, RoutedEventArgs args)
    {
        Profiles.SelectedItem = null;
        ShowDraft(new DeviceWarningProfile { Id = Guid.NewGuid().ToString("N"), Name = "" });
    }

    private void ProfileChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_loading && _catalog is not null && Profiles.SelectedIndex >= 0)
        {
            ShowDraft(_catalog.Profiles[Profiles.SelectedIndex]);
        }
    }

    private async Task LoadAsync(string? selectedId = null)
    {
        _catalog = await _runtime.SettingsProfileApi.GetDeviceWarningProfilesAsync(_runtime.StopToken);

        var options = _catalog.Profiles.Select(profile => new ProfileOption(profile)).ToArray();
        var selected = options.FirstOrDefault(item => item.Profile.Id == selectedId) ?? options.FirstOrDefault();

        _loading = true;

        try
        {
            Profiles.ItemsSource = options.Select(item => item.Label).ToArray();
            Profiles.SelectedIndex = Array.IndexOf(options, selected);
        }
        finally
        {
            _loading = false;
        }

        ShowDraft(selected?.Profile);
        Status.Text = $"Default thresholds use the RTX {_catalog.GpuModel} preset.";
    }

    private void ShowDraft(DeviceWarningProfile? profile)
    {
        _draft = profile;
        Editor.IsEnabled = !_busy && profile is not null;
        if (profile is null)
        {
            return;
        }

        _loading = true;
        try
        {
            ProfileName.Text = profile.Name;
            Enabled.IsChecked = profile.Enabled;
            Source.SelectedIndex = Array.FindIndex(Sources, item => item.Id == profile.PluginId);

            PopulateDevices(profile.DeviceId);

            Connector.SelectedIndex = profile.ConnectorId == "2" ? 1 : 0;

            UseDefaults.IsChecked = profile.Limits is null;

            SetLimits(profile.Limits ?? DefaultLimits());

            OverridePolicy.IsChecked = profile.NotificationPolicy is not null;

            _policy.Load(profile.NotificationPolicy ?? _catalog!.DefaultPolicy);

            Shutdown.IsChecked = profile.ShutdownAfterSeconds is not null;

            var seconds = profile.ShutdownAfterSeconds ?? 30;
            Minutes.Value = seconds / 60;
            Seconds.Value = seconds % 60;

            DeleteButton.IsEnabled = _catalog!.Profiles.Any(item => item.Id == profile.Id);

            UpdateEditor();
        }
        finally
        {
            _loading = false;
        }
    }

    private void SourceChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_loading && _draft is not null && Source.SelectedIndex >= 0)
        {
            PopulateDevices(null);
            if (UseDefaults.IsChecked == true)
            {
                SetLimits(DefaultLimits());
            }

            UpdateEditor();
        }
    }

    private void PopulateDevices(string? selectedId)
    {
        var source = Sources[Source.SelectedIndex].Id;
        var available = _catalog!.Targets.Where(target => target.PluginId == source).ToList();

        if (selectedId is not null && available.All(target => target.DeviceId != selectedId))
        {
            available.Add(new(source, selectedId, $"{selectedId} (unavailable)"));
        }

        available.Insert(0, new(source, "", "Automatic (one reporting device)"));
        _devices = [.. available];

        Device.ItemsSource = _devices.Select(item => item.Name).ToArray();
        Device.SelectedIndex = Math.Max(0, Array.FindIndex(_devices, item => item.DeviceId == selectedId));
    }

    private ConnectorWarningLimits DefaultLimits() => Sources[Source.SelectedIndex].Id == "asus-astral"
        ? _catalog!.AstralDefaults : _catalog!.Defaults;

    private void DefaultsChanged(object sender, RoutedEventArgs args)
    {
        if (!_loading && _draft is not null)
        {
            if (UseDefaults.IsChecked == true)
            {
                SetLimits(DefaultLimits());
            }

            UpdateEditor();
        }
    }

    private void OptionsChanged(object sender, RoutedEventArgs args)
    {
        if (!_loading && _draft is not null)
        {
            UpdateEditor();
        }
    }

    private void UpdateEditor()
    {
        var msi = Sources[Source.SelectedIndex].Id == "msi-psu";
        Connector.Visibility = msi ? Visibility.Visible : Visibility.Collapsed;
        LowVoltage.Visibility = HighVoltage.Visibility = msi ? Visibility.Collapsed : Visibility.Visible;
        foreach (var input in Limits.Children.OfType<NumberBox>())
        {
            input.IsEnabled = UseDefaults.IsChecked != true;
        }

        PolicyHost.IsEnabled = OverridePolicy.IsChecked == true;
        Minutes.IsEnabled = Seconds.IsEnabled = Shutdown.IsChecked == true;
    }

    private void SetLimits(ConnectorWarningLimits limits)
    {
        Overcurrent.Value = limits.OvercurrentAmperes;
        OvercurrentDuration.Value = limits.OvercurrentMilliseconds;
        Activation.Value = limits.ImbalanceActivationAmperes;
        Imbalance.Value = limits.ImbalancePercentage;
        ImbalanceDuration.Value = limits.ImbalanceMilliseconds;
        LowVoltage.Value = limits.LowPinVoltage;
        HighVoltage.Value = limits.HighPinVoltage;
    }

    private async void Save_Click(object sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        if (_draft is not { } draft || Source.SelectedIndex < 0)
        {
            return;
        }

        var source = Sources[Source.SelectedIndex];
        var device = Device.SelectedIndex < 0 ? null : _devices[Device.SelectedIndex].DeviceId;
        var limits = UseDefaults.IsChecked == true ? null : new ConnectorWarningLimits(Overcurrent.Value, Whole(OvercurrentDuration),
            Whole(Activation), Whole(Imbalance), Whole(ImbalanceDuration), LowVoltage.Value, HighVoltage.Value);
        var profile = draft with
        {
            Name = ProfileName.Text.Trim(),
            PluginId = source.Id,
            DeviceId = string.IsNullOrEmpty(device) ? null : device,
            ConnectorId = source.Id == "msi-psu" ? (Connector.SelectedIndex == 1 ? "2" : "1") : null,
            Enabled = Enabled.IsChecked == true,
            Limits = limits,
            NotificationPolicy = OverridePolicy.IsChecked == true ? _policy.Build() : null,
            ShutdownAfterSeconds = Shutdown.IsChecked == true ? Whole(Minutes) * 60 + Whole(Seconds) : null
        };

        if (!profile.IsValid())
        {
            throw new ArgumentException("Enter a profile name and valid thresholds. A shutdown delay must be between 00:01 and 1439:59.");
        }

        var previous = _catalog?.Profiles.FirstOrDefault(item => item.Id == profile.Id);
        await _runtime.SettingsProfileApi.SaveDeviceWarningProfileAsync(profile, _runtime.StopToken);
        await _runtime.EventLog.RecordAsync($"Warning profile ({profile.Name})", previous, profile);
        await LoadAsync(profile.Id);
        Status.Text = $"Saved {profile.Name}.";
    });

    private async void Delete_Click(object sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        if (_draft is { } profile)
        {
            await _runtime.SettingsProfileApi.DeleteDeviceWarningProfileAsync(profile.Id, _runtime.StopToken);
            await _runtime.EventLog.RecordAsync<DeviceWarningProfile?>($"Warning profile ({profile.Name})", profile, null);
            await LoadAsync();
            Status.Text = $"Deleted {profile.Name}.";
        }
    });

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        Editor.IsEnabled = AddButton.IsEnabled = Profiles.IsEnabled = false;
        try
        {
            await action();
        }
        catch (Exception error) when (error is ArgumentException or MonitoringApiException or HttpRequestException
            or InvalidDataException or NotSupportedException or OperationCanceledException or System.Text.Json.JsonException)
        {
            Status.Text = error.Message;
        }
        finally
        {
            _busy = false;
            AddButton.IsEnabled = Profiles.IsEnabled = _catalog is not null;
            Editor.IsEnabled = _draft is not null;
        }
    }

    private static int Whole(NumberBox input) => input.Value is >= 0 and <= 30000 && input.Value == Math.Truncate(input.Value)
        ? (int)input.Value : throw new ArgumentException($"{input.Header} requires a whole number.");

    private sealed record SourceOption(string Id, string Label);

    private sealed record ProfileOption(DeviceWarningProfile Profile)
    {
        public string Label => Profile.Enabled ? Profile.Name : $"{Profile.Name} (disabled)";
    }
}
