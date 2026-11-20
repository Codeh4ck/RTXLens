using System.Threading.Channels;
using RTXLens.Core.Diagnostics;
using RTXLens.Hardware.Nvidia;

namespace RTXLens.Service.Configuration;

internal sealed class ProfileGpuDiscovery(TimeProvider clock, ILogger<ProfileGpuDiscovery> logger, NvidiaTelemetryReader? reader = null) : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(1);
    private DeviceList? _latest;

    public NvidiaDevice[] GetCachedDevices()
    {
        if (reader is null)
        {
            return [];
        }

        var latest = Volatile.Read(ref _latest);
        var age = latest is null ? TimeSpan.MaxValue : clock.GetElapsedTime(latest.Timestamp);

        if (age >= RefreshInterval)
        {
            _requests.Writer.TryWrite(true);
        }

        // Allow a refresh in progress to finish without briefly dropping the active profile.
        return latest is not null && age < RefreshInterval * 2 ? latest.Devices : [];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (reader is null)
        {
            return;
        }

        var failed = false;
        await foreach (var request in _requests.Reader.ReadAllAsync(stoppingToken))
        {
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            if (_latest is { } cached && clock.GetElapsedTime(cached.Timestamp) < RefreshInterval)
            {
                continue;
            }

            NvidiaDevice[] devices;
            try
            {
                devices = reader.GetDevices();
                if (failed)
                {
                    logger.LogActivity(LogLevel.Information, "gpu.profile-detection-recovered", "NVIDIA",
                        "GPU detection for chart profiles recovered.");
                    failed = false;
                }
            }
            catch (Exception error)
            {
                devices = [];
                if (!failed)
                {
                    logger.LogActivity(LogLevel.Warning, "gpu.profile-detection-failed", "NVIDIA",
                        "Could not identify the GPU for chart profiles. The service will retry.", eventViewer: true, exception: error);
                    failed = true;
                }
            }

            Volatile.Write(ref _latest, new DeviceList(devices, clock.GetTimestamp()));
        }
    }

    private sealed record DeviceList(NvidiaDevice[] Devices, long Timestamp);
}
