using System.Text.Json.Serialization;
using RTXLens.Contracts.Errors;
using RTXLens.Plugins.WireView.Contracts.Common;
using RTXLens.Plugins.WireView.Contracts.Input;
using RTXLens.Plugins.WireView.Contracts.Output;

namespace RTXLens.Plugins.WireView.Serialization;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DeviceConfig))]
[JsonSerializable(typeof(DeviceDisplayConfig))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(ConfigRequest))]
[JsonSerializable(typeof(ClearFaultsRequest))]
[JsonSerializable(typeof(ConfigResponse))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(RawConfigResponse))]
[JsonSerializable(typeof(SensorResponse))]
internal partial class WireViewJsonContext : JsonSerializerContext;
