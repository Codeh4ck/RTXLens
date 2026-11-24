namespace RTXLens.App.Platform.Startup;

internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activation;
    private RegisteredWaitHandle? _activationListener;
    private readonly EventWaitHandle _warnings;
    private RegisteredWaitHandle? _warningListener;

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
        _warnings = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Warnings");
        _activation = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Activate");
    }

    public void RequestWarnings() => _warnings.Set();

    public void ListenForWarnings(Action show) =>
        _warningListener = ThreadPool.RegisterWaitForSingleObject(_warnings, (_, _) => show(), null, Timeout.Infinite, false);

    public void RequestActivation() => _activation.Set();

    public void ListenForActivation(Action activate) =>
        _activationListener = ThreadPool.RegisterWaitForSingleObject(_activation, (_, _) => activate(), null,
            Timeout.Infinite, executeOnlyOnce: false);

    public void Dispose()
    {
        _activationListener?.Unregister(null);

        _warningListener?.Unregister(null);
        _warnings.Dispose();
        _activation.Dispose();
        _mutex.Dispose();
    }
}
