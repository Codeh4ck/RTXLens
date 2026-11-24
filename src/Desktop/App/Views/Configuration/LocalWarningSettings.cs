using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RTXLens.App.Platform;
using RTXLens.App.ViewModels.Shell;
using RTXLens.Presentation.Settings;
using static RTXLens.App.Views.Configuration.ConfigurationPage;
using static RTXLens.App.Views.Shell.DesktopControls;

namespace RTXLens.App.Views.Configuration;

internal static class LocalWarningSettings
{
    public static Control Create(ShellViewModel shell)
    {
        var preferences = shell.Preferences;
        var directory = shell.PreferencesDirectory;
        var customPath = Path.Combine(directory, "warning-custom.wav");

        var popup = Switch("Show popups on this PC");
        popup.IsChecked = preferences.WarningNotifications;

        var sound = Switch("Play warning sound");
        sound.IsChecked = preferences.PlayWarningSound;

        var loop = Switch("Loop sound while a warning is active");
        loop.IsChecked = preferences.LoopWarningSound;

        var options = new List<string> { "Warning1", "Warning2", "Warning3", "Warning4" };
        if (File.Exists(customPath) || preferences.WarningSound == "Custom")
        {
            options.Add("Custom");
        }

        var picker = new ComboBox { ItemsSource = options.ToArray(), SelectedItem = preferences.WarningSound };
        var volume = new Slider { Minimum = 0, Maximum = 100, TickFrequency = 1, IsSnapToTickEnabled = true, Value = preferences.WarningSoundVolume };
        var percent = Caption($"{preferences.WarningSoundVolume}%");
        var volumeField = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Children = { volume, percent } };

        Grid.SetColumn(percent, 1);

        var feedback = Caption("");
        var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        var browse = ActionButton("Choose WAV file", "add-action", ImportAsync);
        var test = ActionButton("Test popup and sound", "display-action", TestAsync);

        browse.HorizontalAlignment = test.HorizontalAlignment = HorizontalAlignment.Left;
        percent.VerticalAlignment = VerticalAlignment.Center;

        var soundFields = Columns(Field("Warning sound", picker), Field("Volume", volumeField));
        StackAtNarrowWidth(soundFields, 440);

        var soundControls = WarningsConfigurationView.Stack(loop, soundFields, browse);
        BindTo(soundControls, Control.IsEnabledProperty, sound, nameof(ToggleSwitch.IsChecked));

        var deliveryNotice = Alert("Live notifications are turned off. You can still test your popup and sound.");
        var editor = shell.Configuration;

        Watch(deliveryNotice, editor, () => deliveryNotice.IsVisible = editor.CurrentConfiguration?.Warnings.NotificationsEnabled == false, nameof(editor.RevisionText));

        var controls = WarningsConfigurationView.Stack(popup, sound, soundControls, test, feedback);
        var settingsArea = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { controls } };

        settingsArea.ColumnDefinitions[0].MaxWidth = 600;

        var fields = WarningsConfigurationView.Stack(Caption("Saved immediately for this Windows account. The notifier runs even when the dashboard is closed."),
            deliveryNotice, settingsArea);

        debounce.Tick += async (_, _) =>
        {
            debounce.Stop(); await SaveVolume();
        };

        fields.DetachedFromVisualTree += async (_, _) =>
        {
            if (!debounce.IsEnabled)
            {
                return;
            }

            debounce.Stop(); await SaveVolume();
        };

        popup.IsCheckedChanged += async (_, _) => await shell.ChangePreferencesAsync(p => p with
        {
            WarningNotifications = popup.IsChecked == true, WarningSoundEnabled = p.PlayWarningSound
        });

        sound.IsCheckedChanged += async (_, _) => await shell.ChangePreferencesAsync(p => p with { WarningSoundEnabled = sound.IsChecked == true });
        loop.IsCheckedChanged += async (_, _) => await shell.ChangePreferencesAsync(p => p with { LoopWarningSound = loop.IsChecked == true });

        Avalonia.Automation.AutomationProperties.SetName(picker, "Warning sound");
        Avalonia.Automation.AutomationProperties.SetName(volume, "Warning sound volume");

        picker.SelectionChanged += async (_, _) =>
        {
            if (picker.SelectedItem is string name)
            {
                await shell.ChangePreferencesAsync(p => p with { WarningSound = name });
            }
        };

        volume.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty)
            {
                return;
            }

            percent.Text = $"{volume.Value:0}%";

            debounce.Stop();
            debounce.Start();
        };

        return Section("Alerts on this PC", fields);

        Task<bool> SaveVolume() => shell.ChangePreferencesAsync(p => p with { WarningSoundVolume = (int)Math.Round(volume.Value) });

        async Task TestAsync()
        {
            debounce.Stop();
            if (!await SaveVolume())
            {
                return;
            }

            if (shell.Preferences is { WarningNotifications: false, PlayWarningSound: false })
            {
                feedback.Text = "Enable popups or sound to preview a warning.";
                return;
            }

            if (OperatingSystem.IsWindows() && EventWaitHandle.TryOpenExisting(@"Local\" + DesktopBuild.InstanceName + ".Notifier.Preview", out var signal))
            {
                using (signal)
                {
                    signal.Set();
                }

                feedback.Text = "Preview sent to the background notifier.";
            }
            else
            {
                feedback.Text = "The warning notifier is offline. Start RTX Lens Notifier or sign in again.";
            }
        }

        async Task ImportAsync()
        {
            if (TopLevel.GetTopLevel(picker) is not { } window)
            {
                return;
            }

            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose a warning sound", FileTypeFilter = [new FilePickerFileType("WAV audio") { Patterns = ["*.wav"] }]
            });

            if (files.Count == 0)
            {
                return;
            }

            var pending = Path.Combine(directory, $"warning-{Guid.NewGuid():N}.tmp");
            try
            {
                await using var input = await files[0].OpenReadAsync();

                var header = new byte[12];
                await input.ReadExactlyAsync(header);

                if (Encoding.ASCII.GetString(header, 0, 4) != "RIFF" || Encoding.ASCII.GetString(header, 8, 4) != "WAVE")
                {
                    throw new InvalidDataException("Choose a valid WAV audio file.");
                }

                Directory.CreateDirectory(directory);
                await using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await output.WriteAsync(header);
                    await input.CopyToAsync(output);
                }

                File.Move(pending, customPath, true);

                if (!options.Contains("Custom"))
                {
                    options.Add("Custom");
                    picker.ItemsSource = options.ToArray();
                }

                picker.SelectedItem = "Custom";
                feedback.Text = "Custom sound imported.";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                feedback.Text = "Could not import the sound. " + error.Message;
            }
            finally
            {
                try
                {
                    File.Delete(pending);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {

                }
            }
        }
    }
}
