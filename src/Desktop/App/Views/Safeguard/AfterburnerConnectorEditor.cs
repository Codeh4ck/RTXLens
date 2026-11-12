using Avalonia.Controls;
using Avalonia.Layout;
using RTXLens.App.Controls;
using RTXLens.Plugins.Msi.Contracts.Safeguard;
using RTXLens.Presentation.Msi.Afterburner;
using static RTXLens.App.Views.Configuration.ConfigurationPage;
using static RTXLens.App.Views.Shell.DesktopControls;

namespace RTXLens.App.Views.Safeguard;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class AfterburnerConnectorEditor : UserControl
{
    private readonly int _connector;
    private readonly string _installation;
    private readonly AfterburnerConnectorInspection _saved;
    private readonly ToggleSwitch _enabled;
    private readonly ValidatedNumberInput _timeout;
    private readonly ComboBox _action;
    private readonly ComboBox _scripts;
    private readonly ContentControl _alert = Alert();
    private SafeguardBinding? _binding;

    public AfterburnerConnectorEditor(int connector, string installation, AfterburnerConnectorInspection saved)
    {
        _connector = connector;
        _installation = installation;
        _saved = saved;
        _binding = saved.ScriptBindingId is { } id ? new SafeguardStorage().Read<SafeguardBinding>("bindings", id) : null;
        _enabled = Switch($"Enable alarm for connector {connector}");
        _enabled.IsChecked = saved.AlarmConfigured;
        _timeout = new ValidatedNumberInput("Alarm timeout", saved.AlarmTimeoutMs, 0, int.MaxValue)
        {
            PlaceholderText = "Afterburner default", Increment = 100
        };
        _action = new ComboBox
        {
            ItemsSource = new[] { "Keep existing action", "Run emergency low-power profile", "Run bundled script" },
            SelectedIndex = saved.LowPowerAction ? 1 : _binding is not null ? 2 : 0
        };
        _scripts = new ComboBox
        {
            ItemsSource = SafeguardScripts.All.Select(script => script.Name).ToArray(),
            SelectedIndex = _binding is null ? -1 : SafeguardScripts.All.ToList().FindIndex(script => script.Id == _binding.ScriptId),
            PlaceholderText = "Select a bundled script"
        };
        var configure = ActionButton("Configure", "settings-action", async void () => await ConfigureAsync());
        var scriptRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Children = { _scripts, configure } };
        Grid.SetColumn(configure, 1);
        var simulation = Alert("Validation only. Simulation sends a real email without changing GPU settings or shutting down Windows.", "warning");
        var savedAction = saved.LowPowerAction ? "Emergency low-power profile"
            : saved.BuiltInScriptFileName ?? (string.IsNullOrWhiteSpace(saved.Application) ? "None"
                : $"{(saved.Application == "%AB%" ? "Afterburner" : saved.Application)} {saved.Arguments}".Trim());
        var fields = Columns(Field("Alarm timeout (ms)", _timeout), Field("Alarm action", _action));
        fields.ColumnDefinitions[0].Width = new(135);
        Content = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Caption($"Saved alarm: {saved.Status} · Action: {savedAction}"),
                _enabled, fields, scriptRow, simulation, _alert
            }
        };

        _action.SelectionChanged += (_, _) => UpdateActions();
        _scripts.SelectionChanged += (_, _) => UpdateActions();
        UpdateActions();

        void UpdateActions()
        {
            scriptRow.IsVisible = _action.SelectedIndex == 2;
            configure.IsEnabled = SelectedScript is not null;
            simulation.IsVisible = scriptRow.IsVisible && SelectedScript?.IsSimulation == true;
        }
    }

    public bool IsDirty
    {
        get
        {
            try
            {
                var draft = Read();
                return draft.Enabled != _saved.AlarmConfigured || draft.AlarmTimeoutMs != _saved.AlarmTimeoutMs
                    || draft.UseLowPowerAction != _saved.LowPowerAction
                    || (draft.Script is not null && draft.Script.Id != _saved.ScriptBindingId);
            }
            catch (ArgumentException)
            {
                return true;
            }
        }
    }

    private SafeguardScriptDefinition? SelectedScript => _scripts.SelectedIndex >= 0 ? SafeguardScripts.All[_scripts.SelectedIndex] : null;

    public AfterburnerConnectorSettings Read()
    {
        var timeout = (int?)_timeout.ReadOptional();
        if (timeout is null && _saved.AlarmTimeoutMs is not null)
        {
            throw new ArgumentException($"Enter an alarm timeout for connector {_connector}.");
        }

        var lowPower = _action.SelectedIndex == 1 || _action.SelectedIndex == 0 && _saved.LowPowerAction;
        var script = _action.SelectedIndex == 2 ? _binding : null;
        if (_action.SelectedIndex == 2 && (script is null || script.ScriptId != SelectedScript?.Id))
        {
            throw new ArgumentException($"Configure the selected script for connector {_connector}.");
        }

        return new(_connector, _enabled.IsChecked == true, lowPower) { AlarmTimeoutMs = timeout, Script = script };
    }

    private async Task ConfigureAsync()
    {
        if (SelectedScript is not { } script || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        try
        {
            var window = new SafeguardScriptWindow(script, _binding, _installation, _connector);
            if (await window.ShowDialog<SafeguardBinding?>(owner) is { } binding)
            {
                _binding = binding;
                SetAlert(_alert, $"Shutdown delay: {binding.DelayMs} ms" + (binding.MailId.HasValue ? " · SMTP configured" : ""), "success");
            }
        }
        catch (Exception error) when (SafeguardScriptWindow.IsConfigurationError(error))
        {
            SetAlert(_alert, error.Message, "error");
        }
    }
}
