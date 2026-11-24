using RTXLens.Hardware.Nvidia;
using RTXLens.Plugins.Asus.Contracts.Common;
using RTXLens.Plugins.Asus.Device;

namespace RTXLens.Tests.TestDoubles;

internal sealed class TestAsusReader(AsusTestState state) : IAsusGpuReader
{
    public IReadOnlyList<AsusDeviceInfo> Devices => state.Connected
        ? state.Data.Select(data => new AsusDeviceInfo(data.DeviceId, data.HardwareName)).ToArray() : [];

    public AsusGpuData? Read(string? deviceId, CancellationToken cancellationToken, NvidiaPollTimings? timings = null)
    {
        if (Interlocked.Increment(ref state.ActiveOperations) != 1)
        {
            Interlocked.Increment(ref state.ConcurrentAccessDetected);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref state.PollCount);
            state.LastRequestedDeviceId = deviceId;
            if (state.FailReads)
            {
                throw new IOException("Simulated GPU read failure.");
            }

            if (!state.Connected)
            {
                return null;
            }

            return deviceId is null ? state.Data.FirstOrDefault() : state.Data.FirstOrDefault(data => data.DeviceId == deviceId);
        }
        finally
        {
            Interlocked.Decrement(ref state.ActiveOperations);
        }
    }

    public void Dispose() => Interlocked.Increment(ref state.DisposeCount);
}
