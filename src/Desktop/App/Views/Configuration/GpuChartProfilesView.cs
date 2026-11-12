using System.Net;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using RTXLens.App.ViewModels.Shell;
using RTXLens.Client.Http;
using RTXLens.Contracts.Configuration;
using static RTXLens.App.Views.Configuration.ConfigurationPage;
using static RTXLens.App.Views.Configuration.WarningsConfigurationView;
using static RTXLens.App.Views.Shell.DesktopControls;

namespace RTXLens.App.Views.Configuration;

internal sealed class GpuChartProfilesView : ProfileRequestView
{
    private readonly ComboBox _profiles = new()
    {
        Width = 220,
        PlaceholderText = "Select a profile",
        VerticalAlignment = VerticalAlignment.Center,
        ItemTemplate = new FuncDataTemplate<ProfileOption>((item, _) => new TextBlock
        {
            Text = item?.Profile.Name,
            TextTrimming = TextTrimming.CharacterEllipsis
        })
    };
    private readonly NumericUpDown _current = Number(1, 30, 0.1m);
    private readonly NumericUpDown _power = Number(1, 250, 0.1m);
    private readonly NumericUpDown _powerFallback = Number(1, 250, 0.1m);
    private readonly NumericUpDown _voltageMin = Number(5, 11.99m, 0.1m);
    private readonly NumericUpDown _voltageMax = Number(12.01m, 20, 0.1m);
    private readonly Button _use;
    private readonly Button _add;
    private readonly Button _delete;
    private readonly Button _save;
    private readonly StackPanel _fields;
    private SettingsProfileCatalog? _catalog;
    private SettingsProfile? _draft;
    private bool _loading;

    public GpuChartProfilesView(ShellViewModel shell) : base(shell)
    {
        AutomationProperties.SetName(_profiles, "GPU chart profile");
        _use = ActionButton("Use selected profile", "wireview-apply", async void () => await RunAsync(UseSelectedAsync));
        _use.Classes.Add("desktop-action");
        _use.VerticalAlignment = VerticalAlignment.Center;
        _add = IconButton("Add GPU chart profile", "add-action", async void () =>
        {
            if (TopLevel.GetTopLevel(this) is Window owner &&
                await RequestProfileNameAsync(owner, "New GPU chart profile") is { } name)
            {
                await RunAsync(api => AddAsync(api, name));
            }
        });
        _delete = IconButton("Delete GPU chart profile", "chart-clear", async void () =>
        {
            if (_profiles.SelectedItem is ProfileOption { IsCustom: true, Profile: var profile }
                && TopLevel.GetTopLevel(this) is Window owner
                && await ConfirmAsync(owner, "Delete GPU chart profile", $"Delete {profile.Name}?", "Delete"))
            {
                await RunAsync(api => DeleteAsync(api, profile));
            }
        });

        _save = CommitButton("Save configuration", async void () => await RunAsync(SaveAsync));
        _power.PlaceholderText = "Use cable rating";

        _fields = Stack(
            Columns(Field("Current chart maximum per pin (A)", _current), Field("Power chart maximum per pin (W, optional override)", _power)),
            Field("Power chart maximum per pin without a cable rating (W)", _powerFallback),
            Columns(Field("Voltage chart minimum (V)", _voltageMin), Field("Voltage chart maximum (V)", _voltageMax)),
            new StackPanel { Orientation = Orientation.Horizontal, Children = { _save } });
        var panel = Section("GPU chart profiles", Stack(
            Caption("Choose the chart scales used by the desktop and widget. Auto follows the detected GPU."),
            _fields, Status));
        panel.HeaderActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _profiles, _add, _delete, _use }
        };
        Content = panel;

        _profiles.SelectionChanged += (_, _) =>
        {
            if (!_loading && _profiles.SelectedItem is ProfileOption selected)
            {
                ShowDraft(ResolveProfile(selected));
                Report(_draft is null ? "Auto is waiting for a supported GPU and connector telemetry." : null);
            }
        };

        UpdateAvailability();
        AttachedToVisualTree += async (_, _) =>
        {
            if (_catalog is null)
            {
                await RunAsync(LoadAsync);
            }
        };
    }

    protected override void UpdateAvailability()
    {
        _profiles.IsEnabled = _use.IsEnabled = !IsBusy && _catalog is not null;
        _fields.IsEnabled = _add.IsEnabled = !IsBusy && _draft is not null;
        var isCustom = _profiles.SelectedItem is ProfileOption { IsCustom: true };
        _delete.IsEnabled = !IsBusy && isCustom;
        ToolTip.SetTip(_save, isCustom ? "Save changes to this custom profile."
            : "Use these scales until the backend restarts. Add a custom profile to save them permanently.");
    }

    private async Task LoadAsync(IMonitoringApi api)
    {
        var catalog = await api.GetSettingsProfilesAsync(Shell.StopToken);
        LoadCatalog(catalog, (_profiles.SelectedItem as ProfileOption)?.Profile.Id);
        Report(_draft is null ? "Auto is waiting for a supported GPU and connector telemetry." : null);
    }

    private async Task UseSelectedAsync(IMonitoringApi api)
    {
        if (_profiles.SelectedItem is not ProfileOption { Profile: var selected })
        {
            throw new ArgumentException("Select a profile first.");
        }

        var previous = _catalog;
        var catalog = await api.ApplySettingsProfileAsync(new SettingsProfileRequest(selected.Id, null, false), Shell.StopToken);
        await Shell.LocalEvents.RecordAsync("GPU chart profile", new { previous?.SelectedId, previous?.Effective },
            new { SelectedId = (string?)catalog.SelectedId, catalog.Effective });
        LoadCatalog(catalog);
        Report(null);
    }

    private async Task AddAsync(IMonitoringApi api, string name)
    {
        if (_profiles.SelectedItem is not ProfileOption selected)
        {
            return;
        }

        var profile = Build() with { Name = name };
        var catalog = await api.ApplySettingsProfileAsync(new SettingsProfileRequest(selected.Profile.Id, profile, true), Shell.StopToken);
        LoadCatalog(catalog);
        await Shell.LocalEvents.RecordAsync<SettingsProfile?>("GPU chart profile", null, catalog.Effective);
        Report($"Created {name}.", "success");
    }

    private async Task SaveAsync(IMonitoringApi api)
    {
        if (_profiles.SelectedItem is not ProfileOption selected)
        {
            return;
        }

        var previous = selected.Profile;
        var profile = Build();
        if (selected.IsCustom)
        {
            profile = profile with { Id = previous.Id };
            try
            {
                var catalog = await api.UpdateSettingsProfileAsync(profile, Shell.StopToken);
                LoadCatalog(catalog, previous.Id);
                await Shell.LocalEvents.RecordAsync("GPU chart profile", previous, profile);
                Report($"Saved {profile.Name}.", "success");
                return;
            }
            catch (MonitoringApiException error) when (error.StatusCode == HttpStatusCode.NotFound)
            {
                var catalog = await api.GetSettingsProfilesAsync(Shell.StopToken);
                if (!catalog.Profiles.Any(item => item.Id == previous.Id))
                {
                    LoadCatalog(catalog);
                    Report("This profile was removed. Choose another profile and try again.", "warning");
                    return;
                }

                if (catalog.SelectedId != previous.Id)
                {
                    Report("Update the backend service to save this profile. The active profile was kept.", "warning");
                    return;
                }
            }
        }

        var applied = await api.ApplySettingsProfileAsync(new SettingsProfileRequest(previous.Id, profile, false), Shell.StopToken);
        await Shell.LocalEvents.RecordAsync("GPU chart session configuration", _catalog?.Effective, applied.Effective);
        LoadCatalog(applied);
        Report(selected.IsCustom
            ? "Applied for this session. Update the backend service to save custom profiles permanently."
            : "Applied for this service session.", selected.IsCustom ? "warning" : "info");
    }

    private async Task DeleteAsync(IMonitoringApi api, SettingsProfile profile)
    {
        try
        {
            var catalog = await api.DeleteSettingsProfileAsync(profile.Id, Shell.StopToken);
            LoadCatalog(catalog);
            await Shell.LocalEvents.RecordAsync<SettingsProfile?>("GPU chart profile", profile, null);
            Report($"Deleted {profile.Name}.", "success");
        }
        catch (MonitoringApiException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            var catalog = await api.GetSettingsProfilesAsync(Shell.StopToken);
            LoadCatalog(catalog, profile.Id);
            Report(catalog.Profiles.Any(item => item.Id == profile.Id)
                ? "Update the backend service to delete custom chart profiles."
                : "This profile was already removed.", "warning");
        }
    }

    private void LoadCatalog(SettingsProfileCatalog catalog, string? selectedId = null)
    {
        _catalog = catalog;
        var profiles = new[] { SettingsProfile.Rtx5090 with { Id = "auto", Name = "Auto" } }.Concat(catalog.Profiles)
            .Select(profile => new ProfileOption(profile)).ToArray();
        var selected = profiles.FirstOrDefault(profile => profile.Profile.Id == selectedId)
            ?? profiles.FirstOrDefault(profile => profile.Profile.Id == catalog.SelectedId) ?? profiles[0];

        _loading = true;
        _profiles.ItemsSource = profiles;
        _profiles.SelectedItem = selected;
        _loading = false;

        ShowDraft(ResolveProfile(selected));
    }

    private SettingsProfile? ResolveProfile(ProfileOption selected)
    {
        if (selected.Profile.Id == _catalog?.SelectedId && _catalog.Effective is { } effective)
        {
            return effective;
        }

        return selected.Profile.Id == "auto" ? _catalog?.Automatic : selected.Profile;
    }

    private void ShowDraft(SettingsProfile? profile)
    {
        _draft = profile;
        _current.Value = profile is null ? null : (decimal)profile.CurrentCeiling;
        _power.Value = profile?.PowerCeilingPerPin is { } power ? (decimal)power : null;
        _powerFallback.Value = profile is null ? null : (decimal)profile.PowerFallbackPerPin;
        _voltageMin.Value = profile is null ? null : (decimal)profile.VoltageMinimum;
        _voltageMax.Value = profile is null ? null : (decimal)profile.VoltageMaximum;
        UpdateAvailability();
    }

    private SettingsProfile Build()
    {
        var original = _draft ?? throw new ArgumentException("Select a GPU chart profile.");
        var profile = original with
        {
            CurrentCeiling = ReadNumber(_current, "the current chart maximum"),
            PowerCeilingPerPin = _power.Value is { } power ? (double)power : null,
            PowerFallbackPerPin = ReadNumber(_powerFallback, "the fallback power chart maximum"),
            VoltageMinimum = ReadNumber(_voltageMin, "the voltage chart minimum"),
            VoltageMaximum = ReadNumber(_voltageMax, "the voltage chart maximum")
        };
        if (!profile.IsValid())
        {
            throw new ArgumentException("Enter chart scales within the supported ranges.");
        }

        return profile;
    }

    private sealed record ProfileOption(SettingsProfile Profile)
    {
        public bool IsCustom => Guid.TryParseExact(Profile.Id, "N", out _);
    }
}
