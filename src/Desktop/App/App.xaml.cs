using Microsoft.UI.Xaml;
using RTXLens.App.Platform.Interop;
using RTXLens.App.Platform.Startup;
using RTXLens.App.Platform.Tray;
using RTXLens.App.Platform.Windowing;
using RTXLens.App.Services;
using RTXLens.Presentation.Settings;

namespace RTXLens.App;

public sealed partial class App : Application
{
    private SingleInstance? _instance;
    private MainWindow? _window;
    private AppRuntime? _runtime;
    private TrayService? _tray;
    private bool _exiting;
    private bool _exitRequestPending;
    private bool _hidden;

    public App()
    {
        UnhandledException += (_, error) => File.WriteAllText(Path.Combine(Path.GetTempPath(), "rtxlens-error.txt"), error.Exception.ToString());
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var arguments = Environment.GetCommandLineArgs();
        if (arguments is [_, "--afterburner-config", var operationId, var requestPath, var requestHash])
        {
            await Msi.Afterburner.AfterburnerElevatedOperation.RunHelperAsync(operationId, requestPath, requestHash);
            Exit();
            return;
        }
#if TELEMETRY_DEBUG
        var demo = arguments.Contains("--demo");
        var diagnostic = demo && arguments.Contains("--smoke-test");
#else
        var demo = false;
        var diagnostic = false;
#endif
        _instance = new SingleInstance(diagnostic);
        if (!_instance.IsFirst)
        {
            SingleInstance.ShowExisting();
            _instance.Dispose();
            Exit();
            return;
        }

        // Keep this ID in sync with the installer's Start menu shortcut.
        _ = NativeMethods.SetCurrentProcessExplicitAppUserModelID("Codeh4ck.RTXLens");

        _runtime = new AppRuntime(demo, Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(), diagnostic);
        _window = new MainWindow(_runtime);
        _window.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "monitor.ico"));
        _window.AppWindow.Closing += (_, closing) =>
        {
            if (_exiting)
            {
                return;
            }

            closing.Cancel = true;
            if (_exitRequestPending)
            {
                return;
            }

            if (_runtime.Preferences.CloseToTray && _tray?.IsAvailable == true)
            {
                _runtime.SaveWindowPlacement(_window);
                Hide();
            }
            else
            {
                ExitApplication();
            }
        };
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        _tray = new TrayService(handle, _window.DispatcherQueue, Show, () => { Show(); _window.Navigate("settings"); },
            ExitApplication, minimized =>
            {
                if (minimized)
                {
                    _runtime.SetRendering(false);
                    if (_runtime.Preferences.MinimizeToTray && _tray?.IsAvailable == true)
                    {
                        Hide();
                    }
                }
                else if (!_hidden)
                {
                    _runtime.SetRendering(true);
                }
            });
        _runtime.TrackWindow(_window, "main", diagnostic ? WindowSizeLimits.MaximumWidth : AppPreferences.DefaultWindowWidth,
            diagnostic ? WindowSizeLimits.MaximumHeight : AppPreferences.DefaultWindowHeight, center: false);
        _window.Activate();
        if (_window.ThemeRoot.XamlRoot is null)
        {
            var loaded = new TaskCompletionSource();
            void OnLoaded(object sender, RoutedEventArgs eventArgs)
            {
                _window.ThemeRoot.Loaded -= OnLoaded;
                loaded.TrySetResult();
            }
            _window.ThemeRoot.Loaded += OnLoaded;
            await loaded.Task;
        }
        await _runtime.StartAsync(_window, _tray);
#if TELEMETRY_DEBUG
        if (arguments.Contains("--smoke-test") && _runtime.IsDemo)
        {
            var output = arguments.FirstOrDefault(argument => argument.StartsWith("--smoke-output=", StringComparison.Ordinal))?[15..]
                ?? Path.Combine(Path.GetTempPath(), "rtxlens-smoke");
            try { await Diagnostics.SmokeTestRunner.RunAsync(_window, _runtime, output); }
            catch (Exception exception)
            {
                Directory.CreateDirectory(output);
                await File.WriteAllTextAsync(Path.Combine(output, "smoke-error.txt"), exception.ToString());
                Environment.ExitCode = 1;
            }

            await ExitCoreAsync();
            return;
        }
#endif
        if (_runtime.Preferences.StartMinimized || arguments.Contains("--minimized"))
        {
            if (_tray.IsAvailable)
            {
                Hide();
            }
            else
            {
                Platform.Interop.NativeMethods.ShowWindow(handle, Platform.Interop.NativeConstants.MinimizeWindow);
            }
        }
    }

    private void Show()
    {
        if (_window is null || _runtime is null)
        {
            return;
        }

        _hidden = false;
        _window.AppWindow.Show();
        Platform.Interop.NativeMethods.ShowWindow(WinRT.Interop.WindowNative.GetWindowHandle(_window), Platform.Interop.NativeConstants.RestoreWindow);
        _runtime.SetRendering(true);
        _window.Activate();
    }

    private void Hide()
    {
        _hidden = true;
        _runtime?.SetRendering(false);
        _window?.AppWindow.Hide();
    }

    private async void ExitApplication()
    {
        if (_exiting || _exitRequestPending || _runtime is null || _window is null)
        {
            return;
        }

        _exitRequestPending = true;
        try
        {
            if (_runtime.Settings.ConfirmBeforeExit)
            {
                Show();
                if (!await ExitConfirmation.ConfirmAsync(_window.ThemeRoot, _runtime.Settings))
                {
                    return;
                }
            }

            if (_runtime.AfterburnerWindow is { } integration && !await integration.ConfirmDiscardAsync())
            {
                return;
            }

            await ExitCoreAsync();
        }
        finally
        {
            _exitRequestPending = false;
        }
    }

    private async Task ExitCoreAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        if (_runtime is not null && _window is not null)
        {
            await _window.CloseViewsAsync();
            try { await _runtime.StopAsync(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { System.Diagnostics.Debug.WriteLine(exception); }
        }
        _tray?.Dispose();
        _instance?.Dispose();
        _window?.Close();
        Exit();
    }
}
