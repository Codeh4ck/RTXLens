using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RTXLens.App.Themes;
using RTXLens.App.Platform;
using RTXLens.App.ViewModels.Appearance;
using RTXLens.App.ViewModels.Preview;
using RTXLens.App.ViewModels.Shell;
using RTXLens.App.Views.Preview;
using RTXLens.App.Views.Shell;
using RTXLens.Presentation.Settings;

namespace RTXLens.App;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var directory = Path.Combine(localAppData, DesktopBuild.DataDirectoryName, "Avalonia");
            var appearance = new AppearanceViewModel(this, new AppearanceStore(Path.Combine(directory, "appearance.json")));

            if (desktop.Args?.Contains("--controls-preview", StringComparer.Ordinal) == true)
            {
                desktop.MainWindow = new ControlsPreviewWindow { DataContext = new ControlsPreviewViewModel(appearance) };
            }
            else
            {
                var preferences = new PreferencesStore(Path.Combine(directory, "preferences.json"));
                var model = new ShellViewModel(preferences, appearance);
                var currentWarnings = desktop.Args?.Contains("--warnings", StringComparer.Ordinal) == true;
                var window = new ShellWindow(model, desktop.Args?.Contains("--minimized", StringComparer.Ordinal) == true, currentWarnings);

                window.Opened += async (_, _) => await model.StartAsync();
                desktop.MainWindow = window;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
