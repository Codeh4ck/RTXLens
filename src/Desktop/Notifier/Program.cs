using Avalonia;
using RTXLens.App.Platform;

namespace RTXLens.Notifier;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var preview = args is ["--preview"];

        using var instance = new Mutex(false, @"Local\" + DesktopBuild.InstanceName + ".Notifier", out var first);
        using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + DesktopBuild.InstanceName + ".Notifier.Preview");

        if (!first)
        {
            if (preview)
            {
                signal.Set();
            }
            return;
        }
        if (preview)
        {
            signal.Set();
        }

        PreviewSignal = signal;
        AppBuilder.Configure<NotifierApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }

    internal static EventWaitHandle PreviewSignal { get; private set; } = null!;
}
