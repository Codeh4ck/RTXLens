using System.Globalization;

namespace RTXLens.Presentation.Msi.Afterburner;

public static class AfterburnerConfiguration
{
    public const string PluginFile = "Plugins/Monitoring/PSU.cfg";
    public const string ProfileFile = "Profiles/MSIAfterburner.cfg";
    public static IReadOnlyList<string> FileNames { get; } = Array.AsReadOnly([PluginFile, ProfileFile]);

    public static AfterburnerInspection Inspect(byte[] pluginBytes, byte[] profileBytes)
    {
        var plugin = new IniDocument(pluginBytes);
        var profile = new IniDocument(profileBytes);
        if (!plugin.HasSection("Settings") || !profile.HasSection("Settings") || profile.Get("Settings", "Sources") is null)
        {
            throw new InvalidDataException("The active Afterburner configuration is incomplete. Configure Afterburner once, or restore a working backup.");
        }

        if (plugin.Get("Settings", "DataLayout") is { } and not ("0" or "3"))
        {
            throw new InvalidDataException("The PSU plugin is forced to a non-TS data layout. Select automatic detection in Afterburner's PSU configuration first.");
        }

        foreach (var key in new[] { "DataLayout", "GPUSafeguard", "SoftwareAlarm", "MaxCurrent", "CurrentDif" })
        {
            _ = plugin.Get("Settings", key);
        }

        foreach (var connectorNumber in new[] { 1, 2 })
        {
            foreach (var key in new[] { "AlarmThresholdMin", "AlarmThresholdMax", "AlarmFlags", "AlarmTimeout", "AlarmApp", "AlarmAppCmdLine", "Formula" })
            {
                _ = profile.Get(AlarmSection(connectorNumber), key);
            }
        }

        var connector = IsConfigured(profile, 2) && !IsConfigured(profile, 1) ? 2 : 1;
        var section = AlarmSection(connector);

        var settings = new AfterburnerSettings(
            Boolean(profile, "Monitoring", "PSU.dll", false),
            Boolean(plugin, "Settings", "GPUSafeguard", true) && IsConfigured(profile, connector),
            Boolean(plugin, "Settings", "SoftwareAlarm", true),
            Number(plugin, "Settings", "MaxCurrent", 9.5),
            Number(plugin, "Settings", "CurrentDif", 5),
            connector,
            IsLowPowerAction(profile, section));
        var action = (profile.Get(section, "AlarmApp") ?? "") + " " + (profile.Get(section, "AlarmAppCmdLine") ?? "");

        return new(settings, IsConfigured(profile, 1), IsConfigured(profile, 2), action.Trim(),
            "Existing Afterburner sound flags are preserved; alarm timeout is connector-specific.");
    }

    public static AfterburnerChangeSet Plan(string installation, byte[] pluginBytes, byte[] profileBytes, AfterburnerSettings settings)
    {
        settings.Validate();
        _ = Inspect(pluginBytes, profileBytes);
        var plugin = new IniDocument(pluginBytes);
        var profile = new IniDocument(profileBytes);

        var alarms = settings.GetConnectorAlarms();
        var original = alarms.ToDictionary(alarm => alarm.Connector, alarm => InspectConnector(profileBytes, alarm.Connector));
        var enabledChanged = alarms.Any(alarm => alarm.Enabled != original[alarm.Connector].AlarmConfigured);
        if (enabledChanged)
        {
            var anyEnabled = new[] { 1, 2 }.Any(connector =>
                alarms.FirstOrDefault(alarm => alarm.Connector == connector)?.Enabled ?? IsConfigured(profile, connector));
            plugin.Set("Settings", "GPUSafeguard", anyEnabled ? "1" : "0");
        }

        plugin.Set("Settings", "SoftwareAlarm", settings.SoftwareAlarm ? "1" : "0");
        plugin.Set("Settings", "MaxCurrent", ((float)settings.MaxCurrent).ToString("G", CultureInfo.InvariantCulture));
        plugin.Set("Settings", "CurrentDif", ((float)settings.CurrentDifference).ToString("G", CultureInfo.InvariantCulture));
        profile.Set("Monitoring", "PSU.dll", settings.MonitoringEnabled ? "1" : "0");

        foreach (var alarm in alarms)
        {
            var saved = original[alarm.Connector];
            var section = AlarmSection(alarm.Connector);
            if (alarm.AlarmTimeoutMs is { } timeout && timeout != saved.AlarmTimeoutMs)
            {
                profile.Set(section, "AlarmTimeout", timeout.ToString(CultureInfo.InvariantCulture));
            }

            var actionChanged = alarm.UseLowPowerAction != saved.LowPowerAction
                || (alarm.Script is { } binding && (binding.Id != saved.ScriptBindingId
                    || (OperatingSystem.IsWindows()
                        && !saved.Arguments.StartsWith(AfterburnerSafeguard.HiddenWindowOption, StringComparison.OrdinalIgnoreCase))));
            if (alarm.Enabled == saved.AlarmConfigured && !actionChanged)
            {
                continue;
            }

            var flags = Integer(profile, section, "AlarmFlags", 0);
            if (flags is not (0 or 3))
            {
                throw new InvalidDataException("Connector " + alarm.Connector
                    + " uses unverified native alarm options. Review it in Afterburner first; no files were changed.");
            }

            if (alarm.Enabled != saved.AlarmConfigured)
            {
                if (alarm.Enabled)
                {
                    EnableSource(profile, "PSU 12VHPWR" + alarm.Connector + " alarm");
                    profile.Set(section, "AlarmFlags", "3");
                }

                profile.Set(section, "AlarmThresholdMin", "");
                profile.Set(section, "AlarmThresholdMax", alarm.Enabled ? "1" : "");
            }

            if (actionChanged)
            {
                if (alarm.Script is { } script)
                {
                    script.Validate();
                    if (!OperatingSystem.IsWindows() || script.Connector != alarm.Connector || alarm.UseLowPowerAction)
                    {
                        throw new InvalidDataException("The selected script does not match this connector.");
                    }

                    profile.Set(section, "AlarmFlags", "3");
                    profile.Set(section, "AlarmApp", AfterburnerSafeguard.PowerShell);
                    profile.Set(section, "AlarmAppCmdLine", AfterburnerSafeguard.Arguments(
                        Plugins.Msi.Contracts.Safeguard.SafeguardScripts.Get(script.ScriptId), script.Id));
                }
                else if (alarm.UseLowPowerAction)
                {
                    profile.Set(section, "AlarmFlags", "3");
                    profile.Set(section, "AlarmApp", "%AB%");
                    profile.Set(section, "AlarmAppCmdLine", "-lowpowerprofile -message GPUSafeguard");
                }
                else
                {
                    // Only remove our recognized native preset. An existing custom command is preserved.
                    profile.Set(section, "AlarmApp", "");
                    profile.Set(section, "AlarmAppCmdLine", "");
                }
            }
        }

        var afterPlugin = plugin.ToBytes();
        var afterProfile = profile.ToBytes();
        _ = Inspect(afterPlugin, afterProfile);

        return new(installation, [new(PluginFile, pluginBytes, afterPlugin), new(ProfileFile, profileBytes, afterProfile)]);
    }

    public static AfterburnerConnectorInspection InspectConnector(byte[] profileBytes, int connector)
    {
        if (connector is not (1 or 2))
        {
            throw new ArgumentException("Select connector 1 or 2.");
        }

        var profile = new IniDocument(profileBytes);
        var section = AlarmSection(connector);
        var source = "PSU 12VHPWR" + connector + " alarm";
        var enabled = (profile.Get("Settings", "Sources") ?? "").Split(',')
            .Any(item => item.Trim().Equals("+" + source, StringComparison.OrdinalIgnoreCase));

        var application = profile.Get(section, "AlarmApp") ?? "";
        var arguments = profile.Get(section, "AlarmAppCmdLine") ?? "";
        var script = OperatingSystem.IsWindows() ? AfterburnerSafeguard.Inspect(application, arguments) : null;
        return new(connector, enabled, IsConfigured(profile, connector), IsLowPowerAction(profile, section),
            profile.Get(section, "AlarmThresholdMin") ?? "", profile.Get(section, "AlarmThresholdMax") ?? "",
            profile.Get(section, "AlarmFlags") ?? "0", profile.Get(section, "AlarmTimeout") ?? "Default",
            application, arguments, profile.Get(section, "Formula") ?? "")
        {
            BuiltInScriptFileName = script?.FileName,
            ScriptBindingId = script?.Id
        };
    }

    public static string AlarmSection(int connector) => "Source PSU 12VHPWR" + connector + " alarm";

    private static bool IsConfigured(IniDocument profile, int connector)
    {
        var name = "PSU 12VHPWR" + connector + " alarm";
        var section = AlarmSection(connector);
        var sources = (profile.Get("Settings", "Sources") ?? "").Split(',');

        return Boolean(profile, "Monitoring", "PSU.dll", false)
            && sources.Any(source => source.Trim().Equals("+" + name, StringComparison.OrdinalIgnoreCase))
            && profile.Get(section, "AlarmThresholdMax") == "1"
            && string.IsNullOrWhiteSpace(profile.Get(section, "Formula"))
            && Integer(profile, section, "AlarmFlags", 0) == 3;
    }

    private static void EnableSource(IniDocument profile, string name)
    {
        var current = profile.Get("Settings", "Sources");
        if (string.IsNullOrWhiteSpace(current))
        {
            throw new InvalidDataException("The active Afterburner monitoring source list is missing. Open and configure Afterburner once first.");
        }

        var sources = current.Split(',');
        var matches = sources.Select((source, index) => (Name: source.Trim().TrimStart('+', '-'), Index: index))
            .Where(source => source.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (matches.Length > 1)
        {
            throw new InvalidDataException("The selected alarm appears more than once in the monitoring source list.");
        }

        if (matches.Length == 1)
        {
            var index = matches[0].Index;
            var source = sources[index];
            var start = source.Length - source.TrimStart().Length;
            var length = source.Trim().Length;
            sources[index] = source[..start] + "+" + name + source[(start + length)..];
            profile.Set("Settings", "Sources", string.Join(',', sources));
        }
        else
        {
            profile.Set("Settings", "Sources", current + ",+" + name);
        }

        if (!string.IsNullOrWhiteSpace(profile.Get(AlarmSection(name.Contains('1') ? 1 : 2), "Formula")))
        {
            throw new InvalidDataException("The selected alarm has a custom formula. Remove it in Afterburner before enabling Safeguard+ here.");
        }
    }

    private static bool IsLowPowerAction(IniDocument profile, string section) =>
        string.Equals(profile.Get(section, "AlarmApp"), "%AB%", StringComparison.OrdinalIgnoreCase)
        && profile.Get(section, "AlarmAppCmdLine") == "-lowpowerprofile -message GPUSafeguard";

    private static bool Boolean(IniDocument document, string section, string key, bool fallback)
    {
        var value = document.Get(section, key);
        return value is null ? fallback : value switch
        {
            "0" => false,
            "1" => true,
            _ => throw new InvalidDataException("Unsupported boolean setting: " + section + "/" + key)
        };
    }

    private static int Integer(IniDocument document, string section, string key, int fallback)
    {
        var value = document.Get(section, key);
        return value is null ? fallback : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number : throw new InvalidDataException("Invalid integer setting: " + section + "/" + key);
    }

    private static double Number(IniDocument document, string section, string key, double fallback)
    {
        var value = document.Get(section, key);
        return value is null ? fallback : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && double.IsFinite(number) ? number : throw new InvalidDataException("Invalid current setting: " + section + "/" + key);
    }
}