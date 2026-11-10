using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RTXLens.App.Services;
using RTXLens.Client.Http;
using RTXLens.Contracts.Configuration;

namespace RTXLens.App.Views;

public sealed partial class SettingsProfilesView : UserControl
{
    private readonly AppRuntime _runtime;
    private readonly NumberBox _current, _power, _powerFallback, _voltageMinimum, _voltageMaximum;
    private SettingsProfileCatalog? _catalog;
    private SettingsProfile? _draft;
    private bool _busy;
    private bool _loaded;

    internal SettingsProfilesView(AppRuntime runtime)
    {
        _runtime = runtime;
        InitializeComponent();
        _current = AddField("Current chart maximum per pin (A)");
        _power = AddField("Power chart maximum per pin (W, optional override)");
        _powerFallback = AddField("Power chart maximum per pin without a cable rating (W)");
        _voltageMinimum = AddField("Voltage chart minimum (V)");
        _voltageMaximum = AddField("Voltage chart maximum (V)");
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        try
        {
            if (!_loaded)
            {
                _loaded = true;
                await LoadAsync();
            }
        }
        catch (Exception)
        {
            // ignored
        }
    }

    private async void Refresh(object sender, RoutedEventArgs args) => await LoadAsync();
    private async void UseProfile(object sender, RoutedEventArgs args) => await SubmitAsync(false, false);
    private async void ApplySession(object sender, RoutedEventArgs args) => await SubmitAsync(true, false);
    private async void SaveProfile(object sender, RoutedEventArgs args) => await SubmitAsync(true, true);

    private async Task LoadAsync() => await RunAsync(async () =>
    {
        var catalog = await _runtime.SettingsProfileApi.GetSettingsProfilesAsync(_runtime.StopToken);
        LoadCatalog(catalog);

        Status.Text = _catalog?.Effective is { } effective ? $"Active chart profile: {effective.Name}" : "Auto is waiting for a supported GPU and connector telemetry.";
    });

    private async Task SubmitAsync(bool includeEdits, bool save) => await RunAsync(async () =>
    {
        if (ProfilePicker.SelectedItem is not SettingsProfile selected)
        {
            throw new ArgumentException("Select a profile first.");
        }

        var previous = _catalog;
        var settings = includeEdits ? Build(save) : null;
        var catalog = await _runtime.SettingsProfileApi.ApplySettingsProfileAsync(new SettingsProfileRequest(selected.Id, settings, save), _runtime.StopToken);

        await _runtime.EventLog.RecordAsync("GPU chart profile", new { previous?.SelectedId, previous?.Effective },
            new { SelectedId = (string?)catalog.SelectedId, catalog.Effective });
        LoadCatalog(catalog);

        Status.Text = save ? $"Saved and selected {settings!.Name}." : includeEdits ? "Applied for this service session." : $"Selected {selected.Name}.";
    });

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        UseButton.IsEnabled = false;
        Editor.IsEnabled = false;

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
            UseButton.IsEnabled = _catalog is not null;
            Editor.IsEnabled = _draft is not null;
        }
    }

    private void LoadCatalog(SettingsProfileCatalog catalog)
    {
        _catalog = catalog;

        var profiles = new[] { SettingsProfile.Rtx5090 with { Id = "auto", Name = "Auto" } }.Concat(catalog.Profiles).ToArray();

        ProfilePicker.ItemsSource = profiles;
        ProfilePicker.SelectedItem = profiles.FirstOrDefault(profile => profile.Id == catalog.SelectedId) ?? profiles[0];

        ShowDraft(catalog.Effective ?? (catalog.SelectedId == "auto" ? null : ProfilePicker.SelectedItem as SettingsProfile));
    }

    private void ProfileChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ProfilePicker.SelectedItem is SettingsProfile profile)
        {
            ShowDraft(profile.Id == "auto" ? _catalog?.Automatic : profile);
        }
    }

    private void ShowDraft(SettingsProfile? profile)
    {
        _draft = profile;
        Editor.IsEnabled = !_busy && profile is not null;

        if (profile is null)
        {
            return;
        }

        ProfileName.Text = "";
        _current.Value = profile.CurrentCeiling;
        _power.Value = profile.PowerCeilingPerPin ?? double.NaN;
        _powerFallback.Value = profile.PowerFallbackPerPin;
        _voltageMinimum.Value = profile.VoltageMinimum;
        _voltageMaximum.Value = profile.VoltageMaximum;
    }

    private SettingsProfile Build(bool save)
    {
        var original = _draft ?? throw new ArgumentException("Select a GPU settings profile.");
        var name = ProfileName.Text.Trim();

        if (save && name.Length == 0)
        {
            throw new ArgumentException("Enter a name for the saved profile.");
        }

        return original with
        {
            Name = save ? name : original.Name,
            CurrentCeiling = _current.Value,
            PowerCeilingPerPin = Optional(_power),
            PowerFallbackPerPin = _powerFallback.Value,
            VoltageMinimum = _voltageMinimum.Value,
            VoltageMaximum = _voltageMaximum.Value
        };
    }

    private NumberBox AddField(string label)
    {
        var index = Fields.Children.Count;
        if (index % 2 == 0)
        {
            Fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var input = new NumberBox { Header = label, HorizontalAlignment = HorizontalAlignment.Stretch };

        Grid.SetRow(input, index / 2);
        Grid.SetColumn(input, index % 2);

        Fields.Children.Add(input);

        return input;
    }

    private static double? Optional(NumberBox input) => double.IsNaN(input.Value) ? null : input.Value;

}
