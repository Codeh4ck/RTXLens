using System.Text.Json;
using System.Text.Json.Serialization;
using RTXLens.Contracts.Diagnostics;

namespace RTXLens.App.Services;

internal sealed class DesktopEventLog
{
    private const int Capacity = 1000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly string[] PrivateNames = ["password", "secret", "credential", "token", "smtp", "apikey", "privatekey"];
    private readonly string _path;
    private readonly SemaphoreSlim _writeGate = new(1);
    private ServiceLogEntry[] _entries;

    public DesktopEventLog(string path)
    {
        _path = path;

        try
        {
            _entries = File.Exists(path) && new FileInfo(path).Length <= 8 * 1024 * 1024
                ? JsonSerializer.Deserialize<ServiceLogEntry[]>(File.ReadAllText(path))?.TakeLast(Capacity).ToArray() ?? []
                : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Debug.WriteLine(error);
            _entries = [];
        }
    }

    public ServiceLogEntry[] Entries => Volatile.Read(ref _entries);
    public event Action? Changed;

    public async Task ClearAsync(CancellationToken token)
    {
        await _writeGate.WaitAsync(token);

        try
        {
            try
            {
                File.Delete(_path + ".tmp");
                File.Delete(_path);
            }
            catch (DirectoryNotFoundException)
            {
                // There is no saved history if the log directory does not exist.
            }

            Volatile.Write(ref _entries, []);
            Changed?.Invoke();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task RecordAsync<T>(string section, T before, T after)
    {
        var changes = new List<string>();
        try
        {
            Compare(section, JsonSerializer.SerializeToElement(before, Json), JsonSerializer.SerializeToElement(after, Json), changes);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine(error);
            return;
        }

        if (changes.Count == 0)
        {
            return;
        }

        await _writeGate.WaitAsync();

        try
        {
            var timestamp = DateTimeOffset.UtcNow;
            var sequence = _entries.LastOrDefault()?.Sequence ?? long.MinValue;
            var additions = changes.TakeLast(Capacity).Select(message => new ServiceLogEntry(++sequence, timestamp,
                ServiceLogLevel.Information, "desktop.configuration-changed", "Desktop configuration", message, null));
            var entries = _entries.Concat(additions).TakeLast(Capacity).ToArray();

            Volatile.Write(ref _entries, entries);
            Changed?.Invoke();

            var temporary = _path + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(entries));
                File.Move(temporary, _path, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine(error);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static void Compare(string path, JsonElement before, JsonElement after, List<string> changes)
    {
        if (before.ValueKind != JsonValueKind.Undefined && after.ValueKind != JsonValueKind.Undefined
            && JsonElement.DeepEquals(before, after))
        {
            return;
        }

        if (before.ValueKind == JsonValueKind.Object || after.ValueKind == JsonValueKind.Object)
        {
            var old = Properties(before);
            var updated = Properties(after);

            foreach (var name in old.Keys.Union(updated.Keys))
            {
                if (IsPrivate(name))
                {
                    continue;
                }

                Compare($"{path} / {Label(name)}", old.GetValueOrDefault(name), updated.GetValueOrDefault(name), changes);
            }

            return;
        }

        if (before.ValueKind == JsonValueKind.Array || after.ValueKind == JsonValueKind.Array)
        {
            var old = Items(before);
            var updated = Items(after);

            for (var index = 0; index < Math.Max(old.Length, updated.Length); index++)
            {
                Compare($"{path} [{index + 1}]", index < old.Length ? old[index] : default,
                    index < updated.Length ? updated[index] : default, changes);
            }

            return;
        }

        changes.Add($"{path}: {Format(before)} → {Format(after)}");
    }

    private static Dictionary<string, JsonElement> Properties(JsonElement value)
    {
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                properties[property.Name] = property.Value;
            }
        }

        return properties;
    }

    private static JsonElement[] Items(JsonElement value) => value.ValueKind == JsonValueKind.Array
        ? [.. value.EnumerateArray()]
        : [];

    private static bool IsPrivate(string name) => PrivateNames
        .Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));

    private static string Label(string name) => name switch
    {
        "generic-nvidia" => "Nvidia",
        "msi-psu" => "MSI PSU",
        "wireview-pro2" => "WireView Pro II",
        "asus-astral" => "ROG Astral",
        _ => Humanize(name)
    };

    private static string Humanize(string name)
    {
        var label = System.Text.RegularExpressions.Regex.Replace(name, "([a-z0-9])([A-Z])", "$1 $2");
        return label.Length == 0 ? label : char.ToUpperInvariant(label[0]) + label[1..];
    }

    private static string Format(JsonElement value)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => "(unset)",
            JsonValueKind.True => "On",
            JsonValueKind.False => "Off",
            JsonValueKind.String => value.GetString()!,
            _ => value.GetRawText()
        };

        return text.Length <= 250 ? text : text[..249] + "…";
    }
}
