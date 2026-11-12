using System.Text.Json;

namespace RTXLens.App.Themes;

internal sealed class AppearanceStore(string path)
{
    public async Task<AppearanceSettings?> LoadAsync()
    {
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<AppearanceSettings>(stream).ConfigureAwait(false);
    }

    public async Task SaveAsync(AppearanceSettings settings)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("The appearance path has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporary = Path.Combine(directory, $".appearance-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, settings).ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}