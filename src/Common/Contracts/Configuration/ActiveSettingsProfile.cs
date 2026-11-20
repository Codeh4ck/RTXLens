using RTXLens.Contracts.Discovery;

namespace RTXLens.Contracts.Configuration;

public sealed record ActiveSettingsProfile(SettingsProfile Profile, string GpuDeviceId, string? WireViewDeviceId)
{
    public bool AppliesToConnector(GpuAssociation association, string? deviceId) => deviceId is not null && association switch
    {
        GpuAssociation.GpuDevice or GpuAssociation.SelectedGpu => deviceId == GpuDeviceId,
        GpuAssociation.InlineMonitor => deviceId == WireViewDeviceId,
        _ => false
    };
}
