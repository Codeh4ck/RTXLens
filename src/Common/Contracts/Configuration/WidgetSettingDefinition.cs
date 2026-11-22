using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RTXLens.Contracts.Configuration;

public sealed record WidgetSettingDefinition(string Id, string Label, string Group, string Section, string Type,
    JsonElement DefaultValue, int? Min, int? Max, string? Unit, WidgetSettingOption[]? Options, bool Host)
{
    public static IReadOnlyList<WidgetSettingDefinition> Managed { get; } = Load();
    public static IReadOnlyDictionary<string, WidgetSettingDefinition> ById { get; } = Managed.ToDictionary(x => x.Id);
    public string Initial => DefaultValue.ValueKind == JsonValueKind.String ? DefaultValue.GetString()! : DefaultValue.GetRawText();

    private static WidgetSettingDefinition[] Load()
    {
        using var resource = typeof(WidgetSettingDefinition).Assembly.GetManifestResourceStream("WidgetSettings.json")!;

        return
        [
            .. JsonSerializer.Deserialize(resource, WidgetCatalogContext.Default.WidgetSettingDefinitionArray)!.Where(setting => !setting.Host)
        ];
    }

    public bool Accepts(string? value) => value is not null && Type switch
    {
        "switch" => value is "true" or "false",
        "slider" => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= Min && number <= Max,
        "combobox" => Options!.Any(option => option.Key == value),
        "color" => value is ['#', _, _, _, _, _, _] && value.Skip(1).All(char.IsAsciiHexDigit),
        "textfield" => value.Length <= 2000,
        _ => false
    };
}

public sealed record WidgetSettingOption(string Key, string Label)
{
    public override string ToString() => Label;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WidgetSettingDefinition[]))]
internal partial class WidgetCatalogContext : JsonSerializerContext;
