using RTXLens.Core.Diagnostics;

namespace RTXLens.Service.Services;

internal sealed class ServiceRestart(ILogger<ServiceRestart> logger) : IServiceRestart
{
    private int _requested;

    public void RequestRestart()
    {
        if (Interlocked.Exchange(ref _requested, 1) != 0)
        {
            return;
        }

        logger.LogActivity(LogLevel.Information, "service.restart-requested", "Service", "Backend restart requested.");
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            Environment.Exit(1);
        });
    }
}
