using RTXLens.Plugins.GenericNvidia.Contracts.Devices;

namespace RTXLens.Hardware.Nvidia.Diagnostics;

public interface INvidiaDiagnosticsReader
{
    NvidiaDiagnostics? Read(string deviceId);
}
