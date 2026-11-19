using System.Text.Json;

namespace RTXLens.Presentation.Settings;

public sealed class PreferencesStore(string path)
{
    private readonly SemaphoreSlim _gate = new(1);
    public string DirectoryPath { get; } = Path.GetDirectoryName(Path.GetFullPath(path))!;
    public string? LastError { get; private set; }

    public AppPreferences Load()
    {
        try
        {
            LastError = null;
            return File.Exists(path) ? Read(path) : new AppPreferences();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException)
        {
            LastError = $"Preferences could not be loaded; using defaults. {exception.Message}";
            return new AppPreferences();
        }
    }

    private static AppPreferences Read(string source)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(source));
        var value = document.RootElement.Deserialize(PreferencesJsonContext.Default.AppPreferences)
            ?? throw new InvalidDataException("Preferences cannot be null.");

        if (!document.RootElement.TryGetProperty("automaticallyCheckForUpdates", out _))
        {
            value = value with { AutomaticallyCheckForUpdates = true };
        }

        value.Validate();
        return value;
    }

    public async Task SaveAsync(AppPreferences preferences, CancellationToken token = default)
    {
        preferences.Validate();
        await _gate.WaitAsync(token);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(preferences, PreferencesJsonContext.Default.AppPreferences), token);
            File.Move(temporary, path, true);
            LastError = null;
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
