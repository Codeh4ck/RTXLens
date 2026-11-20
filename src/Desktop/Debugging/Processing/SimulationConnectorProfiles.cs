using RTXLens.Plugins.GenericNvidia.Contracts;
using RTXLens.Plugins.Msi.Contracts;
using RTXLens.Plugins.WireView.Contracts;
using RTXLens.Plugins.Asus.Contracts;
namespace RTXLens.Debugging.Processing;

public static class SimulationConnectorProfiles
{
    public static Core.ConnectorProfileResolver CreateResolver() => new(
        new[] { NvidiaIdentity.Id, AsusIdentity.Id, WireViewIdentity.Id, MsiIdentity.Id }
            .Select(id => new SimulationConnectorSourceProvider(id)));
}
