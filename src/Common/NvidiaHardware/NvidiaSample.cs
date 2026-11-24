namespace RTXLens.Hardware.Nvidia;

public sealed record NvidiaSample(NvidiaDevice Device, DateTimeOffset ObservedAt, NvidiaSensor[] Sensors)
{
    public bool CablePinsBelowThreshold { get; private init; }

    internal static NvidiaSample Create(NvidiaDevice device, DateTimeOffset observedAt, NvidiaSensor[] sensors)
    {
        if (!AstralBoard.HasAlternativeInput(device.SubsystemId))
        {
            return new(device, observedAt, sensors);
        }

        var currents = sensors.Where(sensor => sensor.Type == SensorType.Current && sensor.Name.StartsWith("12VHPWR Pin ", StringComparison.Ordinal)).ToArray();

        if (currents.Length == 6 && currents.All(sensor => sensor.Value is >= 0 and <= 0.1f))
        {
            return new(device, observedAt, [.. sensors.Where(sensor => !sensor.Name.StartsWith("12VHPWR ", StringComparison.Ordinal))])
            {
                CablePinsBelowThreshold = true
            };
        }
        return new(device, observedAt, sensors);
    }
}
