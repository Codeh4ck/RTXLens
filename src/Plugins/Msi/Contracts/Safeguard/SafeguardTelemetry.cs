using RTXLens.Contracts.Discovery;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Sensors;

namespace RTXLens.Plugins.Msi.Contracts.Safeguard;

public sealed record SafeguardTelemetry(DateTimeOffset CapturedAt, SensorReading[] Readings)
{
    public Dictionary<string, PluginHardwareMetadata>? Sources { get; init; }
    public ActiveSettingsProfile? SettingsProfile { get; init; }
}
