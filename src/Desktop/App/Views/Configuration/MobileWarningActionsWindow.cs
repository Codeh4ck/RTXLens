using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using RTXLens.App.Views.Shell;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Mobile;
using static RTXLens.App.Views.Configuration.ConfigurationPage;
using static RTXLens.App.Views.Shell.DesktopControls;

namespace RTXLens.App.Views.Configuration;

internal sealed class MobileWarningActionsWindow : LensWindow
{
    private readonly ToggleSwitch _shutdown = Switch("Allow remote shutdown");
    private readonly ToggleSwitch _lowPower = Switch("Allow remote GPU power reduction");
    private readonly ComboBox _gpu = new() { PlaceholderText = "Select a GPU" };
    private readonly Slider _limit = CreatePowerLimitSlider();
    private readonly TextBlock _watts = new() { MinWidth = 60, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _range = Caption();
    private readonly ContentControl _feedback = Alert("Loading warning actions…");
    private readonly ContentControl _mismatch = Alert();
    private readonly Button _save;
    private readonly MobileDevice _device;
    private readonly Func<MobileAccessRequest, Task<MobileAccessResponse?>> _send;
    private MobileWarningActionOptions? _options;
    private PowerTarget[] _targets = [];
    private bool _loading;
    private bool _gpuEdited;
    private bool _saving;
    private bool _closed;

    private PowerTarget? SelectedGpu => _gpu.SelectedIndex >= 0 ? _targets.ElementAtOrDefault(_gpu.SelectedIndex) : null;
    private uint SelectedLimit => checked((uint)Math.Round(_limit.Value * 1000));

    public MobileWarningActionsWindow(MobileDevice device, Func<MobileAccessRequest, Task<MobileAccessResponse?>> send)
    {
        _device = device;
        _send = send;
        Title = "Configure warning actions";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        AutomationProperties.SetName(_limit, "Remote GPU power limit (watts)");

        var watts = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10, Children = { _limit, _watts } };
        Grid.SetColumn(_watts, 1);

        var limitField = Field("Remote GPU power limit", watts);
        limitField.Children.Add(_range);

        _save = CommitButton("Save", SaveAsync);
        _save.IsDefault = true;

        var cancel = ActionButton("Cancel", "cancel-action", () => Close());
        cancel.IsCancel = true;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, _save }
        };

        Body = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = device.Name, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "heading" } },
                Caption("Choose which commands this phone can send during an active warning."),
                new ScrollViewer
                {
                    MaxHeight = 480,
                    Content = new StackPanel
                    {
                        Spacing = 12,
                        Children = { _feedback, _shutdown, _lowPower, Field("Target GPU", _gpu), limitField, _mismatch }
                    }
                },
                buttons
            }
        };

        _shutdown.IsCheckedChanged += (_, _) => UpdateControls();
        _lowPower.IsCheckedChanged += (_, _) => UpdateControls();

        _gpu.SelectionChanged += (_, _) =>
        {
            if (!_loading)
            {
                _gpuEdited = true;
                var saved = _options?.Settings;
                SetSlider(saved?.GpuId == SelectedGpu?.Id ? saved?.PowerMilliwatts : null);
            }
        };

        _limit.ValueChanged += (_, _) => UpdateControls();

        Opened += async (_, _) => await LoadAsync();
        Closed += (_, _) => _closed = true;

        UpdateControls();
    }

    private async Task LoadAsync()
    {
        var response = await _send(new(MobileAccessOperation.GetWarningActions, Id: _device.Id));
        if (_closed)
        {
            return;
        }

        if (response is not { Success: true, WarningActions: { } options })
        {
            SetAlert(_feedback, response?.Error ?? "Could not load warning actions.", "warning");
            return;
        }

        _loading = true;
        _options = options;
        _targets = options.Targets;

        var settings = options.Settings;
        if (settings.GpuId is { } savedGpu && _targets.All(target => target.Id != savedGpu))
        {
            _targets = [.. _targets, new(savedGpu, "Unavailable GPU", 0, 0)];
        }

        _shutdown.IsChecked = settings.EnableShutdown;
        _lowPower.IsChecked = settings.EnableLowPower;

        _gpu.ItemsSource = _targets.Select(target => $"{target.Name} ({target.Id})").ToArray();

        var index = Array.FindIndex(_targets, target => target.Id == settings.GpuId);

        _gpu.SelectedIndex = index >= 0 || _targets.Length != 1 ? index : 0;
        _loading = false;

        SetSlider(index >= 0 ? settings.PowerMilliwatts : null);
        SetAlert(_feedback, options.Targets.Length == 0 ? "No GPU currently reports a writable power limit." : null, "warning");
    }

    private void SetSlider(uint? milliwatts)
    {
        _loading = true;
        SetPowerLimitSlider(_limit, SelectedGpu, milliwatts);
        _loading = false;

        UpdateControls();
    }

    private MobileWarningActionSettings Draft()
    {
        var saved = _options!.Settings;
        var lowPower = _lowPower.IsChecked == true;
        var draft = saved with { EnableShutdown = _shutdown.IsChecked == true, EnableLowPower = lowPower };

        if ((lowPower || _gpuEdited) && SelectedGpu is { Maximum: > 0 } target)
        {
            return draft with { GpuId = target.Id, PowerMilliwatts = SelectedLimit };
        }

        return draft;
    }

    private void UpdateControls()
    {
        if (_loading)
        {
            return;
        }

        var options = _options;
        var gpu = SelectedGpu;
        var ready = options is not null && !_saving;
        var lowPower = _lowPower.IsChecked == true;

        _shutdown.IsEnabled = ready;
        _lowPower.IsEnabled = ready && (lowPower || options!.Targets.Length > 0);
        _gpu.IsEnabled = ready && options!.Targets.Length > 0;
        _limit.IsEnabled = ready && lowPower && gpu is { Maximum: > 0 };

        _watts.Text = gpu is null ? "—" : Watts(SelectedLimit);
        _range.Text = gpu is { Maximum: > 0 } ? $"{Watts(gpu.Minimum)} to {Watts(gpu.Maximum)}" : null;

        _range.IsVisible = _range.Text is not null;

        if (options is null)
        {
            _save.IsEnabled = false;
            return;
        }

        var draft = Draft();

        var keepsUnavailableTarget = options.Settings.EnableLowPower && gpu is { Maximum: 0 } && gpu.Id == options.Settings.GpuId;
        _save.IsEnabled = ready && draft != options.Settings && (!lowPower || (gpu is { Maximum: > 0 } || keepsUnavailableTarget));

        var hasSelectedLimit = lowPower || _gpuEdited || (options.Settings.GpuId == gpu?.Id && options.Settings.PowerMilliwatts is not null);

        var mismatches = hasSelectedLimit && gpu is { Maximum: > 0 }
            ? options.Profiles.Where(profile => profile.GpuId == gpu.Id && profile.PowerMilliwatts != SelectedLimit)
                .Select(profile => $"• {profile.Name} ({Watts(profile.PowerMilliwatts)})").ToArray()
            : [];

        SetAlert(_mismatch, mismatches.Length == 0 ? null
            : "These enabled warning profiles use a different limit. If both limits apply, RTX Lens uses the lower one."
              + Environment.NewLine + string.Join(Environment.NewLine, mismatches), "warning");
    }

    private async Task SaveAsync()
    {
        if (!_save.IsEnabled)
        {
            return;
        }

        var draft = Draft();
        _saving = true;

        UpdateControls();

        var response = await _send(new(MobileAccessOperation.ConfigureWarningActions, Id: _device.Id, WarningActions: draft));
        if (_closed)
        {
            return;
        }

        if (response is { Success: true })
        {
            Close();
            return;
        }

        _saving = false;

        UpdateControls();
        SetAlert(_feedback, response?.Error ?? "Could not save warning actions.", "warning");
    }

    private static string Watts(uint milliwatts) => $"{milliwatts / 1000d:0.###} W";
}
