namespace RTXLens.Contracts.Configuration;

public sealed record WidgetConfiguration : System.Text.Json.Serialization.IJsonOnDeserialized
{
    public Dictionary<string, Dictionary<string, string>> Profiles { get; init; } = [];

    void System.Text.Json.Serialization.IJsonOnDeserialized.OnDeserialized()
    {
        // Older widget profiles may contain the retired connected badge color.
        if (Profiles is null)
        {
            return;
        }

        foreach (var settings in Profiles.Values)
        {
            settings?.Remove("connectedColor");
        }
    }

    public WidgetWarningPreview? Preview { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> ProfileNames => ["Default", .. Profiles.Keys.Where(name => name != "Default").Order(StringComparer.OrdinalIgnoreCase)];

    public static bool IsCustomName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 80 &&
        name == name.Trim() && !name.Any(char.IsControl) &&
        !string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(name, "Instance", StringComparison.OrdinalIgnoreCase);

    public bool IsValid() => Profiles is not null &&
        (Preview is null || ((Preview.Profile == "Default" || IsCustomName(Preview.Profile))
                             && Preview.Scenario != "none" && WidgetSettingDefinition.ById["warningPreview"].Accepts(Preview.Scenario))) &&
        Profiles.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() == Profiles.Count &&
        Profiles.All(profile => (profile.Key == "Default" || IsCustomName(profile.Key)) && profile.Value is not null &&
            profile.Value.All(setting => setting.Key != "warningPreview" && WidgetSettingDefinition.ById.TryGetValue(setting.Key, out var definition)
                && definition.Accepts(setting.Value)));

    public string Value(string profile, WidgetSettingDefinition setting)
    {
        if (Profiles.TryGetValue(profile, out var values) && values.TryGetValue(setting.Id, out var value))
        {
            return value;
        }

        return profile != "Default" ? Value("Default", setting) : setting.Initial;
    }

    public WidgetConfiguration WithValue(string profile, string id, string? value)
    {
        var profiles = new Dictionary<string, Dictionary<string, string>>(Profiles);
        var settings = Profiles.TryGetValue(profile, out var existing) ? new Dictionary<string, string>(existing) : [];

        if (value is null)
        {
            settings.Remove(id);
        }
        else
        {
            settings[id] = value;
        }

        if (settings.Count == 0 && (profile == "Default" || !Profiles.ContainsKey(profile)))
        {
            profiles.Remove(profile);
        }
        else
        {
            profiles[profile] = settings;
        }

        return this with { Profiles = profiles };
    }

    public bool Matches(WidgetConfiguration other) => Preview == other.Preview && Profiles.Count == other.Profiles.Count &&
        Profiles.All(profile => other.Profiles.TryGetValue(profile.Key, out var values) &&
            profile.Value.Count == values.Count && profile.Value.All(pair => values.TryGetValue(pair.Key, out var value) && value == pair.Value));
}

public sealed record WidgetWarningPreview(string Profile, string Scenario, DateTimeOffset IssuedAt);
