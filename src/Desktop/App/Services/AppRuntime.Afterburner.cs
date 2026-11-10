using Microsoft.UI.Windowing;
using RTXLens.App.Msi.Afterburner;
using RTXLens.Presentation.Msi.Afterburner;

namespace RTXLens.App.Services;

internal sealed partial class AppRuntime
{
    internal AfterburnerSettingsWindow? AfterburnerWindow { get; private set; }

    internal static async Task<bool> IsAfterburnerInstalledAsync()
    {
        try
        {
            var selection = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RTXLens", "afterburner-installation.txt");
            var remembered = File.Exists(selection) ? await File.ReadAllTextAsync(selection) : null;
            return await Task.Run(() => AfterburnerDiscovery.Find(remembered).Length > 0);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or ArgumentException or System.ComponentModel.Win32Exception)
        {
            System.Diagnostics.Debug.WriteLine(error);
            return false;
        }
    }

    internal async Task OpenAfterburnerSettingsAsync()
    {
        if (Configuration.Plugins.FirstOrDefault(plugin => plugin.IsMsi)?.Enabled != true
            || !await IsAfterburnerInstalledAsync())
        {
            return;
        }

        if (AfterburnerWindow is null)
        {
            var window = new AfterburnerSettingsWindow(this);
            AfterburnerWindow = window;
            window.Closed += (_, _) => AfterburnerWindow = null;
            TrackWindow(window, "afterburner-settings", 1280, 1000);
        }

        AfterburnerWindow.Activate();
    }
}