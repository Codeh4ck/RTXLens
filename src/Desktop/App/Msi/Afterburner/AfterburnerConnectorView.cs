using Microsoft.UI.Xaml;
using RTXLens.Plugins.Msi.Contracts.Safeguard;
using Microsoft.UI.Xaml.Controls;
using Windows.Globalization.NumberFormatting;
using RTXLens.Presentation.Msi.Afterburner;

namespace RTXLens.App.Msi.Afterburner;

internal sealed partial class AfterburnerConnectorView : UserControl
{
    private readonly CheckBox _enabled;
    private readonly RadioButton _lowPower;
    private readonly NumberBox _timeout;
    private readonly RadioButton _script;
    private readonly ComboBox _scripts;
    private readonly Button _configure;
    private readonly TextBlock _scriptStatus;
    private readonly InfoBar _simulationWarning = new()
    {
        Severity = InfoBarSeverity.Warning,
        IsClosable = false,
        Message = "Validation only. Simulation sends a real email without changing GPU power settings or shutting down the PC."
    };
    private SafeguardBinding? _binding;
    private string? _installation;
    private bool _invalidNumericInput;

    internal int Connector { get; }
    internal bool HasPendingChanges => Saved is { } saved
                                       && (_invalidNumericInput || (_enabled.IsChecked == true) != saved.AlarmConfigured ||
                                           (_lowPower.IsChecked == true) != saved.LowPowerAction
                                           || (_script.IsChecked == true && _scripts.SelectedItem is SafeguardScriptDefinition selected
                                                                         && (_binding?.ScriptId != selected.Id ||
                                                                             _binding?.Id != saved.ScriptBindingId))
                                           || (double.IsFinite(_timeout.Value) ? checked((int)_timeout.Value) : null) != saved.AlarmTimeoutMs);

    internal AfterburnerConnectorSettings Draft
    {
        get
        {
            if (!double.IsFinite(_timeout.Value) && Saved?.AlarmTimeoutMs is not null)
            {
                throw new ArgumentException("Enter an alarm timeout for connector " + Connector + ".");
            }

            if (_script.IsChecked == true && (_scripts.SelectedItem is SafeguardScriptDefinition selected
                ? _binding?.ScriptId != selected.Id : Saved?.LowPowerAction != false))
            {
                throw new ArgumentException("Configure a bundled script for connector " + Connector + ".");
            }

            return new(Connector, _enabled.IsChecked == true, _lowPower.IsChecked == true)
            {
                AlarmTimeoutMs = double.IsFinite(_timeout.Value) ? checked((int)_timeout.Value) : null,
                Script = _script.IsChecked == true && _scripts.SelectedItem is SafeguardScriptDefinition ? _binding : null
            };
        }
    }

    internal AfterburnerConnectorInspection? Saved { get; private set; }

    internal AfterburnerConnectorView(int connector)
    {
        Connector = connector;
        _enabled = new CheckBox { Name = "Connector" + connector + "AlarmEnabled", Content = "Enable alarm for connector " + connector };
        _timeout = new NumberBox
        {
            Name = "Connector" + connector + "AlarmTimeout",
            Header = "Alarm timeout (ms)",
            PlaceholderText = "Afterburner default",
            Minimum = 0,
            Maximum = int.MaxValue,
            SmallChange = 100,
            LargeChange = 1000,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            NumberFormatter = new DecimalFormatter
            {
                IntegerDigits = 1,
                FractionDigits = 0,
                IsGrouped = false,
                NumberRounder = new IncrementNumberRounder
                {
                    Increment = 1,
                    RoundingAlgorithm = RoundingAlgorithm.RoundHalfAwayFromZero
                }
            }
        };

        _lowPower = new RadioButton
        {
            Name = "Connector" + connector + "LowPower",
            GroupName = "Connector" + connector + "Action",
            Content = "Run emergency low-power profile on alarm"
        };

        _script = new RadioButton
        {
            GroupName = _lowPower.GroupName,
            Content = "Run script"
        };

        _scripts = new ComboBox
        {
            PlaceholderText = "Select a Safeguard+ script",
            ItemsSource = SafeguardScripts.All,
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        _configure = new Button { Content = "Configure...", IsEnabled = false, VerticalAlignment = VerticalAlignment.Center };
        var scriptActions = new Grid { ColumnSpacing = 8 };
        scriptActions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        scriptActions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(_configure, 1);

        scriptActions.Children.Add(_scripts);
        scriptActions.Children.Add(_configure);

        _scriptStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(_enabled);
        body.Children.Add(_timeout);
        body.Children.Add(_lowPower);
        body.Children.Add(_script);
        body.Children.Add(scriptActions);
        body.Children.Add(_simulationWarning);
        body.Children.Add(_scriptStatus);

        Content = body;

        _timeout.ValueChanged += OnTimeoutChanged;
        _script.Checked += (_, _) => UpdateScriptControls();
        _script.Unchecked += (_, _) => UpdateScriptControls();
        _scripts.SelectionChanged += (_, _) => UpdateScriptControls();
        _configure.Click += Configure_Click;
    }

    internal void SetInspection(AfterburnerConnectorInspection saved, string installation)
    {
        Saved = saved;
        _installation = installation;
        _binding = saved.ScriptBindingId is { } id ? new SafeguardStorage().Read<SafeguardBinding>("bindings", id) : null;
        _scripts.SelectedItem = SafeguardScripts.All.FirstOrDefault(script => script.FileName == saved.BuiltInScriptFileName);
        _invalidNumericInput = false;
        _enabled.IsChecked = saved.AlarmConfigured;
        _timeout.Value = saved.AlarmTimeoutMs ?? double.NaN;
        _lowPower.IsChecked = saved.LowPowerAction;
        _script.IsChecked = !saved.LowPowerAction;
        _scripts.PlaceholderText = saved.Application.Length > 0 && saved is { LowPowerAction: false, ScriptBindingId: null } ? "Existing custom action (preserved)" : "Select a Safeguard+ script";

        UpdateScriptControls();
    }

    private void UpdateScriptControls()
    {
        var selected = _scripts.SelectedItem as SafeguardScriptDefinition;
        var enabled = _script.IsChecked == true;

        _scripts.IsEnabled = enabled;
        _configure.IsEnabled = enabled && selected is not null && _installation is not null;
        _simulationWarning.IsOpen = enabled && selected?.IsSimulation == true;

        if (enabled && selected is not null && _binding is { } binding && binding.ScriptId == selected.Id)
        {
            var delayLabel = selected.IsSimulation ? "Simulated shutdown delay" : "Shutdown delay";
            _scriptStatus.Text = $"{delayLabel}: {binding.DelayMs:N0} ms" + (selected.SendsMail ? " · SMTP configured" : "") + ". Run Afterburner as administrator.";

            _scriptStatus.Visibility = Visibility.Visible;
        }
        else
        {
            _scriptStatus.Text = string.Empty;
            _scriptStatus.Visibility = Visibility.Collapsed;
        }
    }

    private async void Configure_Click(object sender, RoutedEventArgs args)
    {
        if (_scripts.SelectedItem is not SafeguardScriptDefinition selected || _installation is not { } installation)
        {
            return;
        }

        try
        {
            var previous = _binding is { } existing
                           && (existing.ScriptId == selected.Id || (selected.SendsMail && existing.MailId is not null)) ? existing : null;

            if (await new SafeguardScriptDialog(selected, previous).ShowAsync(XamlRoot, installation, Connector) is { } binding)
            {
                _binding = binding;
                UpdateScriptControls();
            }
        }
        catch (Exception error) when (SafeguardScriptDialog.IsConfigurationError(error))
        {
            _scriptStatus.Text = error.Message;
            _scriptStatus.Visibility = Visibility.Visible;
        }
    }

    internal void CommitPendingEdits() => _invalidNumericInput = !AfterburnerNumericInput.Commit(_timeout);

    private static void OnTimeoutChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsFinite(args.NewValue))
        {
            var rounded = Math.Round(args.NewValue, MidpointRounding.AwayFromZero);
            if (Math.Abs(rounded - args.NewValue) > 0.001)
            {
                sender.Value = rounded;
            }
        }
    }
}