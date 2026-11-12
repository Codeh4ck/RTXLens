using System.Text.Json.Serialization;
using RTXLens.Plugins.GenericNvidia.Contracts.Configuration;
using RTXLens.Plugins.GenericNvidia.Contracts.Devices;
using RTXLens.Plugins.Msi.Contracts.Configuration;
using RTXLens.Plugins.Msi.Contracts.Devices;
using RTXLens.Plugins.Asus.Contracts.Configuration;
using RTXLens.Plugins.Asus.Contracts.Common;
using RTXLens.Contracts.Configuration;
using RTXLens.Contracts.Discovery;
using RTXLens.Contracts.Diagnostics;
using RTXLens.Contracts.Errors;
using RTXLens.Contracts.Health;
using RTXLens.Contracts.Sensors;
using RTXLens.Plugins.WireView.Contracts.Common;
using RTXLens.Plugins.WireView.Contracts.Input;

namespace RTXLens.Client.Serialization;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true, UseStringEnumConverter = true, WriteIndented = true)]
[JsonSerializable(typeof(SensorSnapshot))]
[JsonSerializable(typeof(ServiceLogPage))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(RTXLens.Contracts.Updates.ServiceVersionResponse))]
[JsonSerializable(typeof(RTXLens.Contracts.Updates.ReleaseManifest))]
[JsonSerializable(typeof(RTXLens.Contracts.Updates.ReleaseNotes))]
[JsonSerializable(typeof(PluginCatalog))]
[JsonSerializable(typeof(ConfigurationSnapshot))]
[JsonSerializable(typeof(ServiceConfiguration))]
[JsonSerializable(typeof(PluginSettings))]
[JsonSerializable(typeof(AsusOptions))]
[JsonSerializable(typeof(GenericNvidiaOptions))]
[JsonSerializable(typeof(NvidiaDeviceInfo[]))]
[JsonSerializable(typeof(MsiOptions))]
[JsonSerializable(typeof(RTXLens.Plugins.Msi.Contracts.Telemetry.MsiSnapshot))]
[JsonSerializable(typeof(MsiDeviceInfo[]))]
[JsonSerializable(typeof(AsusDeviceInfo[]))]
[JsonSerializable(typeof(DeviceConfig))]
[JsonSerializable(typeof(ClearFaultsRequest))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(SettingsProfileCatalog))]
[JsonSerializable(typeof(SettingsProfileRequest))]
[JsonSerializable(typeof(SettingsProfile))]
[JsonSerializable(typeof(DeviceWarningProfile))]
[JsonSerializable(typeof(DeviceWarningProfileCatalog))]
[JsonSerializable(typeof(WarningShutdownReason))]
public partial class ClientJsonContext : JsonSerializerContext;
