using RTXLens.Core.Configuration;
using RTXLens.Plugins.WireView.Contracts.Common;
using RTXLens.Plugins.WireView.Device;

namespace RTXLens.Tests.TestDoubles;

internal sealed class TestWireViewReader(WireViewTestState state) : IWireViewDeviceReader
{
    public string PortName => "COM3";

    public WireViewSensorData? TryReadSensors(CancellationToken cancellationToken) =>
        Access(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref state.PollCount);
            return state.Connected ? state.Sample : null;
        });

    public DeviceConfig? ReadConfig() => Access(() => state.ConfigReadFails ? null : state.Configuration);

    public DeviceConfig ApplyConfig(DeviceConfig changes) => Access(() =>
    {
        if (changes.EditToken != state.Configuration.EditToken)
        {
            throw new ConfigurationConflictException();
        }

        state.Configuration = changes with { EditToken = Guid.NewGuid().ToString("N") };
        return state.Configuration;
    });

    public byte[]? ReadConfigRaw() => Access<byte[]?>(() => state.ConfigReadFails ? null : [0xEF, 0x05, 0x02, 0x41]);

    public bool ClearFaults(ushort faultStatusMask, ushort faultLogMask) =>
        Access(() =>
        {
            state.LastStatusMask = faultStatusMask;
            state.LastLogMask = faultLogMask;
            return !state.FaultWriteFails;
        });

    public void Reset()
    {
        // The fake reader does not own a serial handle.
    }

    public void Dispose() => Access(() => Interlocked.Increment(ref state.DisposeCount));

    private T Access<T>(Func<T> operation)
    {
        if (Interlocked.Increment(ref state.ConcurrentOperations) > 1)
        {
            Interlocked.Exchange(ref state.ConcurrentAccessDetected, 1);
        }

        try
        {
            Thread.Sleep(3);
            return operation();
        }
        finally
        {
            Interlocked.Decrement(ref state.ConcurrentOperations);
        }
    }
}
