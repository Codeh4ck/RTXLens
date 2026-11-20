using RTXLens.Plugins.Asus.Contracts;
using RTXLens.Plugins.Asus.Contracts.Warnings;
using System.Globalization;
using RTXLens.Contracts.Alerts;
using RTXLens.Plugins.Asus.Analysis.Monitoring;

namespace RTXLens.Plugins.Asus.Analysis.Mapping;

public static class AsusAlertMapper
{
    public static SensorAlert[] Create(string deviceId, AsusAlertMonitor monitor, DateTimeOffset now)
    {
        var alerts = new List<SensorAlert>(2);
        if (monitor.CurrentImbalance is { } imbalance)
        {
            alerts.Add(new SensorAlert(AsusIdentity.Id, deviceId, AsusAlertKinds.CurrentImbalance, imbalance.Timestamp,
                [Pin(imbalance.MaxPinName, imbalance.MaxPinAmperage), Pin(imbalance.MinPinName, imbalance.MinPinAmperage)],
                imbalance.PercentageDifference));
        }

        if (monitor.WireOvercurrent is { } overcurrent)
        {
            alerts.Add(new SensorAlert(AsusIdentity.Id, deviceId, AsusAlertKinds.WireOvercurrent, now,
                [.. overcurrent.PinValues.Select(pin => Pin(pin.Key, pin.Value))]));
        }

        return [.. alerts];
    }

    private static AlertPin Pin(string name, float value) => new(int.Parse(name.AsSpan(4), CultureInfo.InvariantCulture), value);
}
