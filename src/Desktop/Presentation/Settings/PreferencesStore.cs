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
            if (!File.Exists(path))
            {
                return new AppPreferences();
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var value = document.RootElement.Deserialize(PreferencesJsonContext.Default.AppPreferences)
                ?? throw new InvalidDataException("Preferences cannot be null.");
            if (!document.RootElement.TryGetProperty("windowLayoutVersion", out _))
            {
                value = value with { WindowHeight = Math.Max(value.WindowHeight, 1050), WindowLayoutVersion = 1 };
            }
            if (!document.RootElement.TryGetProperty("automaticallyCheckForUpdates", out _))
            {
                value = value with { AutomaticallyCheckForUpdates = true };
            }

            if (!document.RootElement.TryGetProperty("updateFrequency", out _))
            {
                value = value with { UpdateFrequency = Updates.UpdateFrequency.Daily };
            }

            // The old automatic mode forced the remember-size toggle off.
            if (document.RootElement.TryGetProperty("autoResizeWindows", out var autoResize) && autoResize.ValueKind == JsonValueKind.True)
            {
                value = value with { RememberWindowSize = true };
            }

            value.Validate();
            return value;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            LastError = $"Preferences could not be loaded; using defaults. {exception.Message}";
            return new AppPreferences();
        }
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
