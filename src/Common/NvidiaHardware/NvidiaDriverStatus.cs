namespace RTXLens.Hardware.Nvidia;

internal sealed class NvidiaSessionLostException(string api, int status) : Exception($"{api} lost the GPU driver session (status {status}).");

internal static class NvidiaDriverStatus
{
    internal static int NvApi(int status) => status is -10 or -4 ? throw new NvidiaSessionLostException("NVAPI", status) : status;

    internal static bool NvmlSessionLost(int status) => status is 1 or 9 or 15 or 16 or 18;

    internal static int Nvml(int status) => NvmlSessionLost(status) ? throw new NvidiaSessionLostException("NVML", status) : status;
}

internal sealed class NvidiaQuerySupport<TKey> where TKey : notnull
{
    private readonly HashSet<TKey> _unsupported = [];
    internal bool CanQuery(TKey key) => !_unsupported.Contains(key);

    internal bool NvApi(TKey key, int status)
    {
        if (status is -3 or -104)
        {
            _unsupported.Add(key);
        }

        return NvidiaDriverStatus.NvApi(status) == 0;
    }

    internal bool Nvml(TKey key, int status)
    {
        if (status == 3)
        {
            _unsupported.Add(key);
        }

        return NvidiaDriverStatus.Nvml(status) == 0;
    }
}
