using System.Text.Json.Serialization;
using RTXLens.Contracts.Health;
using RTXLens.Contracts.Diagnostics;
using RTXLens.Contracts.Sensors;
using RTXLens.Contracts.Discovery;

namespace RTXLens.Service;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SensorSnapshot))]
[JsonSerializable(typeof(ServiceLogEntry))]
[JsonSerializable(typeof(ServiceLogPage))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(Contracts.Updates.ServiceVersionResponse))]
[JsonSerializable(typeof(PluginCatalog))]
[JsonSerializable(typeof(RTXLens.Contracts.Configuration.ConfigurationSnapshot))]
[JsonSerializable(typeof(RTXLens.Contracts.Configuration.ServiceConfiguration))]
[JsonSerializable(typeof(RTXLens.Contracts.Configuration.PluginSettings))]
[JsonSerializable(typeof(RTXLens.Contracts.Configuration.SettingsProfile))]
[JsonSerializable(typeof(RTXLens.Contracts.Configuration.SettingsProfileCatalog))]
[JsonSerializable(typeof(RTXLens.Contracts.Configuration.SettingsProfileRequest))]
[JsonSerializable(typeof(RTXLens.Contracts.Configuration.DeviceWarningProfile[]))]
[JsonSerializable(typeof(RTXLens.Contracts.Configuration.DeviceWarningProfileCatalog))]
[JsonSerializable(typeof(RTXLens.Contracts.Configuration.WarningShutdownReason))]
internal partial class AppJsonContext : JsonSerializerContext;
