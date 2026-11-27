using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using RTXLens.Contracts.Configuration;
using RTXLens.Plugins.Asus.Contracts;
using RTXLens.Presentation.Settings;
using static RTXLens.App.Views.Configuration.ConfigurationPage;
using static RTXLens.App.Views.Configuration.WarningsConfigurationView;
using static RTXLens.App.Views.Shell.DesktopControls;

namespace RTXLens.App.Views.Configuration;

internal sealed partial class DeviceWarningProfilesView
{
    private readonly ToggleSwitch _reducePower = Switch("Enable GPU power limit reduction on warning");
    private readonly ToggleSwitch _autoRestore = Switch("Automatically restore previous limit when warning clears");
    private readonly ComboBox _powerGpu = new() { PlaceholderText = "Select the GPU powered by this connector" };
    private readonly Slider _powerLimit = CreatePowerLimitSlider();
    private readonly TextBlock _watts = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 60 };
    private readonly StackPanel _heldActions = new() { Spacing = 6 };

    private PowerTarget[] _powerTargets = [];

    private PowerTarget? SelectedPowerTarget => _powerGpu.SelectedIndex >= 0
                                                && _powerGpu.SelectedIndex < _powerTargets.Length ? _powerTargets[_powerGpu.SelectedIndex] : null;

    private Control BuildReactions()
    {
        AutomationProperties.SetName(_powerLimit, "Reduce power limit to (watts)");

        var refresh = IconButton("Refresh GPU limits", "refresh", () => RunAsync(async api =>
        {
            var catalog = await Shell.LoadWarningProfilesAsync(api);

            var selection = SelectedPowerTarget is { } target
                ? new GpuPowerReaction(target.Id, (uint)Math.Round(_powerLimit.Value * 1000), _autoRestore.IsChecked == true)
                : _draft?.PowerReduction;

            _catalog = catalog;

            RefreshSources();
            PopulatePowerTargets(selection);
            ShowHeld(catalog.HeldLimits);
        }));

        var limit = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 10 };

        Grid.SetColumn(_watts, 1);
        Grid.SetColumn(refresh, 2);

        limit.Children.Add(_powerLimit);
        limit.Children.Add(_watts);
        limit.Children.Add(refresh);

        var powerFields = Columns(Field("Target GPU", _powerGpu), Field("Reduce power limit to", limit));
        StackAtNarrowWidth(powerFields, 640);

        var panel = Section("Warning reactions", Stack(_reducePower, powerFields, _autoRestore, _shutdown, _shutdownFields));
        panel.HeaderActions = IconButton("How warning reactions work", DesktopIcons.Info, async () =>
        {
            if (TopLevel.GetTopLevel(this) is Window owner)
            {
                await ShowInformationAsync(owner, "Warning reactions", UsageGuide(
                    "To reduce load during a connector warning, enable power reduction, choose the GPU fed by this connector and select a limit in watts. Save the profile.",
                    "The slider uses the range reported by the NVIDIA driver, including custom BIOS limits. RTX Lens changes the board power limit only. Voltage curves, fan settings and clock offsets remain under your tuning tool’s control.",
                    "With automatic restoration enabled, the service restores the exact previous limit after the warnings clear. Otherwise choose Refresh GPU limits and Restore previous limit. If another tool changes the cap, RTX Lens releases it.",
                    "A lower board limit may leave an individual pin overloaded. Shutdown runs independently while a real warning persists. In Debug Console, override a connector pin above the saved profile threshold for its configured duration to exercise the physical GPU cap. Reset the override or disable simulation to end the simulated warning."));
            }
        });

        _reducePower.IsCheckedChanged += (_, _) =>
        {
            if (!_loading)
            {
                UpdateAvailability();
            }
        };

        _autoRestore.IsCheckedChanged += (_, _) => UpdateSaveState();

        _powerGpu.SelectionChanged += (_, _) =>
        {
            if (_loading)
            {
                return;
            }

            ConfigureSlider(null); UpdateAvailability();
        };

        _powerLimit.ValueChanged += (_, _) =>
        {
            ShowWatts();
            UpdateSaveState();
        };

        return panel;
    }

    private void PopulatePowerTargets(GpuPowerReaction? selected)
    {
        var previous = _loading;
        _loading = true;

        try
        {
            var device = _devices.ElementAtOrDefault(_device.SelectedIndex)?.DeviceId;
            _powerTargets = [.. (_catalog?.PowerTargets ?? []).Where(t => PluginId != AsusIdentity.Id || string.IsNullOrEmpty(device) || t.Id == device)];

            if (selected is not null && _powerTargets.All(t => t.Id != selected.GpuId))
            {
                _powerTargets = [.. _powerTargets, new(selected.GpuId, "Unavailable GPU", 0, 0)];
            }

            _powerGpu.ItemsSource = _powerTargets.Select(t => $"{t.Name} ({t.Id})").ToArray();

            var index = Array.FindIndex(_powerTargets, t => t.Id == selected?.GpuId);
            _powerGpu.SelectedIndex = index >= 0 ? index : _powerTargets.Length == 1 ? 0 : -1;

            ConfigureSlider(selected);
        }
        finally
        {
            _loading = previous;
        }

        UpdatePowerAvailability();
    }

    private void ConfigureSlider(GpuPowerReaction? selected)
    {
        SetPowerLimitSlider(_powerLimit, SelectedPowerTarget, selected?.TargetMilliwatts);
        ShowWatts();
    }

    private void ShowWatts() => _watts.Text = SelectedPowerTarget is null ? "—" : $"{_powerLimit.Value:0.###} W";

    private void UpdatePowerAvailability()
    {
        var enabled = _reducePower.IsChecked == true;
        _reducePower.IsEnabled = enabled || _powerTargets.Any(t => t.Maximum > 0);

        ToolTip.SetTip(_reducePower, !_reducePower.IsEnabled ? "No GPU currently reports a writable power-limit interface and a valid requested limit. Select Refresh GPU limits to check again." : null);

        _powerGpu.IsEnabled = enabled;
        _powerLimit.IsEnabled = enabled && SelectedPowerTarget is { Maximum: > 0 };
        _autoRestore.IsEnabled = enabled;
    }

    private GpuPowerReaction? ReadReaction()
    {
        if (_reducePower.IsChecked != true)
        {
            return null;
        }

        var target = SelectedPowerTarget ?? throw new ArgumentException("Select the GPU powered by this connector.");
        return new(target.Id, checked((uint)Math.Round(_powerLimit.Value * 1000)), _autoRestore.IsChecked == true);
    }

    private void ShowHeld(HeldGpuLimit[] limits)
    {
        _heldActions.Children.Clear();

        foreach (var limit in limits)
        {
            var button = ActionButton($"Restore previous limit · {limit.Original / 1000m:0.###} W", "refresh", () => RunAsync(async api =>
            {
                await api.RestoreGpuPowerAsync(limit.GpuId, Shell.StopToken);
                Report("Restore requested. See the GPU protection event for the result.", "info");
            }));

            button.HorizontalAlignment = HorizontalAlignment.Left;

            _heldActions.Children.Add(Section($"GPU power limit · {limit.GpuId}", Stack(
                new TextBlock { Text = limit.Restoring
                    ? $"Restore requested: {limit.Original / 1000m:0.###} W · Awaiting driver confirmation"
                    : $"RTX Lens cap: {limit.Applied / 1000m:0.###} W" }, button)));
        }
    }
}
