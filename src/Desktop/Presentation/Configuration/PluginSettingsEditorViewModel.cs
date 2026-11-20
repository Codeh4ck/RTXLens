using RTXLens.Plugins.GenericNvidia.Contracts;
using RTXLens.Plugins.Msi.Contracts;
using RTXLens.Plugins.WireView.Contracts;
using RTXLens.Plugins.Asus.Contracts;
using System.Text.Json;
using RTXLens.Plugins.GenericNvidia.Contracts.Configuration;
using RTXLens.Client.Serialization;
using RTXLens.Plugins.Asus.Contracts.Configuration;
using RTXLens.Plugins.Msi.Contracts.Configuration;
using RTXLens.Contracts.Configuration;
using RTXLens.Presentation.Infrastructure;

namespace RTXLens.Presentation.Configuration;

public sealed class PluginSettingsEditorViewModel : EditableObject
{
    private readonly PluginSettings _original;
    private bool _enabled;
    private double _poll;
    private double _stale;

    public string Id { get; }
    public string Name { get; }
    public bool IsAsus => Id == AsusIdentity.Id;
    public bool IsNvidia => Id == NvidiaIdentity.Id;
    public GenericNvidiaSettingsEditorViewModel Nvidia { get; }
    public bool IsWireView => Id == WireViewIdentity.Id;
    public bool IsMsi => Id == MsiIdentity.Id;
    public int MinimumPollMs => IsMsi ? 1000 : 100;
    public MsiSettingsEditorViewModel Msi { get; }
    public AsusSettingsEditorViewModel Asus { get; }
    public bool Enabled { get => _enabled; set => Edit(ref _enabled, value); }
    public double PollIntervalMs { get => _poll; set => Edit(ref _poll, value); }
    public double StaleAfterMs { get => _stale; set => Edit(ref _stale, value); }

    public PluginSettingsEditorViewModel(string id, string name, PluginSettings settings)
    {
        Id = id;
        Name = id == NvidiaIdentity.Id ? "Nvidia" : name;
        _original = settings;
        _enabled = settings.Enabled;
        _poll = settings.PollIntervalMs;
        _stale = settings.StaleAfterMs;
        var options = IsAsus && settings.Options is { } json ? json.Deserialize(ClientJsonContext.Default.AsusOptions) : null;
        Asus = new AsusSettingsEditorViewModel(options ?? new AsusOptions());
        Asus.Edited += NotifyEdited;
        var nvidiaOptions = IsNvidia && settings.Options is { } nvidiaJson ? nvidiaJson.Deserialize(ClientJsonContext.Default.GenericNvidiaOptions) : null;
        Nvidia = new GenericNvidiaSettingsEditorViewModel(nvidiaOptions ?? new GenericNvidiaOptions());
        Nvidia.Edited += NotifyEdited;
        var msiOptions = IsMsi && settings.Options is { } msiJson ? msiJson.Deserialize(ClientJsonContext.Default.MsiOptions) : null;
        Msi = new MsiSettingsEditorViewModel(msiOptions ?? new MsiOptions());
        Msi.Edited += NotifyEdited;
    }

    public PluginSettings Build()
    {
        var result = _original with
        {
            Enabled = Enabled,
            AutomaticallyDisabled = Enabled == _original.Enabled && _original.AutomaticallyDisabled,
            PollIntervalMs = NumericInput.Integer(PollIntervalMs, Name + " polling interval"),
            StaleAfterMs = NumericInput.Integer(StaleAfterMs, Name + " freshness timeout")
        };
        if (!result.IsValid())
        {
            throw new ArgumentException($"{Name}: polling must be 100–30000 ms; freshness must be at least polling and at most 300000 ms.");
        }

        if (IsAsus)
        {
            result = result with { Options = JsonSerializer.SerializeToElement(Asus.Build(), ClientJsonContext.Default.AsusOptions) };
        }

        if (IsNvidia)
        {
            result = result with { Options = JsonSerializer.SerializeToElement(Nvidia.Build(), ClientJsonContext.Default.GenericNvidiaOptions) };
        }

        if (IsMsi)
        {
            if (result.PollIntervalMs < 1000)
            {
                throw new ArgumentException("MSI polling must be at least 1000 ms.");
            }

            var msi = Msi.Build();

            if (msi.FanControl == MsiFanControl.Curve && result.PollIntervalMs > 5000)
            {
                throw new ArgumentException("MSI fan curve control requires a polling interval of 1000-5000 ms.");
            }

            result = result with { Options = JsonSerializer.SerializeToElement(msi, ClientJsonContext.Default.MsiOptions) };
        }

        return result;
    }

    public bool Matches(PluginSettings settings)
    {
        if (IsNvidia && !Nvidia.Matches(settings.Options?.Deserialize(ClientJsonContext.Default.GenericNvidiaOptions) ?? new GenericNvidiaOptions()))
        {
            return false;
        }

        if (IsMsi && !Msi.Matches(settings.Options is { } msiJson ? msiJson.Deserialize(ClientJsonContext.Default.MsiOptions) ?? new MsiOptions() : new MsiOptions()))
        {
            return false;
        }

        if (!double.IsFinite(PollIntervalMs) || !double.IsFinite(StaleAfterMs) || Enabled != settings.Enabled
            || Math.Abs(PollIntervalMs - settings.PollIntervalMs) > 0.0001 || Math.Abs(StaleAfterMs - settings.StaleAfterMs) > 0.0001)
        {
            return false;
        }

        var options = IsAsus && settings.Options is { } json ? json.Deserialize(ClientJsonContext.Default.AsusOptions) : null;
        return !IsAsus || Asus.Matches(options ?? new AsusOptions());
    }
}
