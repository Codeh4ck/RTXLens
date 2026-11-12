using RTXLens.App.Platform.Interop;

namespace RTXLens.App.Platform.Startup;

internal sealed partial class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
#if AVALONIA_DESKTOP
    private readonly EventWaitHandle _activation;
    private RegisteredWaitHandle? _activationListener;
#endif

    public bool IsFirst { get; }

    public SingleInstance(bool diagnostic = false)
    {
        var name = $@"Local\{DesktopBuild.InstanceName}.{Environment.UserName}";
        if (diagnostic)
        {
            name += ".SmokeTest";
        }

        _mutex = new Mutex(false, name, out var created);
        IsFirst = created;
#if AVALONIA_DESKTOP
        _activation = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Activate");
#endif
    }

    public static void ShowExisting()
    {
        var window = NativeMethods.FindWindowW(null, DesktopBuild.WindowTitle);
        if (window != 0)
        {
            var message = NativeMethods.RegisterWindowMessageW(DesktopBuild.ShowMessageName);
            NativeMethods.PostMessageW(window, message, 0, 0);
        }
    }

#if AVALONIA_DESKTOP
    public void RequestActivation() => _activation.Set();

    public void ListenForActivation(Action activate)
    {
        _activationListener = ThreadPool.RegisterWaitForSingleObject(
            _activation, (_, _) => activate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }
#endif

    public void Dispose()
    {
#if AVALONIA_DESKTOP
        _activationListener?.Unregister(null);
        _activation.Dispose();
#endif
        _mutex.Dispose();
    }
}
