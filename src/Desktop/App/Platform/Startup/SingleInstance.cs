namespace RTXLens.App.Platform.Startup;

internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activation;
    private RegisteredWaitHandle? _activationListener;

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
        _activation = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Activate");
    }

    public void RequestActivation() => _activation.Set();

    public void ListenForActivation(Action activate) =>
        _activationListener = ThreadPool.RegisterWaitForSingleObject(
            _activation, (_, _) => activate(), null, Timeout.Infinite, executeOnlyOnce: false);

    public void Dispose()
    {
        _activationListener?.Unregister(null);
        _activation.Dispose();
        _mutex.Dispose();
    }
}
