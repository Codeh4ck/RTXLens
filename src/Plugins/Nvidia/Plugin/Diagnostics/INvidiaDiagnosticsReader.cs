using RTXLens.Plugins.GenericNvidia.Contracts.Devices;

namespace RTXLens.Plugins.GenericNvidia.Diagnostics;

public interface INvidiaDiagnosticsReader
{
    NvidiaDiagnostics? Read(string deviceId);
}
