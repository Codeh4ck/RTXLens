using Avalonia;
using RTXLens.App.Platform;
using RTXLens.App.Platform.Interop;
using RTXLens.App.Platform.Startup;

namespace RTXLens.App;

internal static class Program
{
    internal static SingleInstance? Instance { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception error)
            {
                WriteCrashReport(error);
            }
        };

        if (OperatingSystem.IsWindows() && args is ["--afterburner-config", var id, var path, var hash])
        {
            Msi.Afterburner.AfterburnerElevatedOperation.RunHelperAsync(id, path, hash).GetAwaiter().GetResult();
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            _ = NativeMethods.SetCurrentProcessExplicitAppUserModelID(DesktopBuild.IsDebug ? "Codeh4ck.RTXLens.Debug" : "Codeh4ck.RTXLens");
        }

        using var instance = OperatingSystem.IsWindows() && !args.Contains("--controls-preview", StringComparer.Ordinal)
            ? new SingleInstance()
            : null;

        if (instance is { IsFirst: false })
        {
            instance.RequestActivation();
            return;
        }

        Instance = instance;
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(args);
    }

    private static void WriteCrashReport(Exception error)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "rtxlens-error.txt");
            File.WriteAllText(path, $"{DateTimeOffset.Now:O} · RTX Lens {typeof(Program).Assembly.GetName().Version}{Environment.NewLine}{error}");
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(failure);
        }
    }
}