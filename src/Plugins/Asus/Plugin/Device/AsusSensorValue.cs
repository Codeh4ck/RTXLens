using RTXLens.Plugins.Asus.Mapping;

namespace RTXLens.Plugins.Asus.Device;

public sealed record AsusSensorValue(AsusSensorDefinition Definition, float? Value, DateTimeOffset? ObservedAt = null);
