using RTXLens.Plugins.Asus.Contracts.Warnings;
using RTXLens.Contracts.Alerts;
using RTXLens.Contracts.Enums;
using RTXLens.Contracts.Sensors;
using RTXLens.Debugging.Models;
using RTXLens.Debugging.ViewModels;
using RTXLens.Plugins.Asus.Contracts.Configuration;
using RTXLens.Plugins.Asus.Analysis.Mapping;
using RTXLens.Plugins.Asus.Analysis.Monitoring;

namespace RTXLens.Debugging.Processing;

internal sealed class DebugAsusAlerts
{
    private readonly AsusAlertMonitor _monitor = new();
    private string? _device;
    private AsusOptions? _options;

    public void Reset()
    {
        _monitor.Reset();
        _device = null;
        _options = null;
    }

    public SensorAlert[] Evaluate(DebugPluginViewModel plugin, List<SensorReading> readings, string device, AsusOptions options, DateTimeOffset now, int staleAfterMs)
    {
        if (_device != device || _options != options)
        {
            Reset();

            _device = device;
            _options = options;
        }

        var currents = readings
            .Where(reading =>
                reading is { Identity: { Component: SensorComponent.GpuPin, Metric: SensorMetric.Current }, Quality: ReadingQuality.Valid } &&
                now - reading.ObservedAt <= TimeSpan.FromMilliseconds(staleAfterMs) && reading.Value.HasValue)
            .ToDictionary(reading => "Pin " + reading.Identity.Channel, reading => (float)reading.Value!.Value);

        _monitor.Evaluate(currents, options, now);

        var alerts = AsusAlertMapper.Create(device, _monitor, now).ToList();
        foreach (var metric in plugin.Metrics.Where(metric => metric.Definition.Metric is SensorMetric.ImbalancePercentage or SensorMetric.ImbalanceDetected or SensorMetric.OvercurrentDetected))
        {
            var definition = metric.Definition;

            if (metric.Mode == DebugMetricMode.Live)
            {
                var value = definition.Metric switch
                {
                    SensorMetric.ImbalancePercentage => _monitor.ImbalancePercentage,
                    SensorMetric.ImbalanceDetected => currents.Count >= 2 ? _monitor.CurrentImbalance is null ? 0d : 1d : null,
                    _ => currents.ContainsKey("Pin " + definition.Channel) ? _monitor.WireOvercurrent?.PinValues.ContainsKey("Pin " + definition.Channel) == true ? 1d : 0d : null
                };

                var index = readings.FindIndex(reading => definition.Matches(reading.Identity));
                var reading = new SensorReading(definition.Identity(plugin.Id, device), definition.Unit, value, now, value.HasValue ? ReadingQuality.Valid : ReadingQuality.Unavailable);

                if (index >= 0)
                {
                    readings[index] = reading;
                }
                else
                {
                    readings.Add(reading);
                }
            }
            else if (definition.Metric == SensorMetric.ImbalanceDetected)
            {
                alerts.RemoveAll(alert => alert.Kind == AsusAlertKinds.CurrentImbalance);

                if (metric is { Mode: DebugMetricMode.Fixed, Value: 1 } && currents.Count >= 2)
                {
                    var highest = currents.MaxBy(pin => pin.Value);
                    var lowest = currents.MinBy(pin => pin.Value);

                    alerts.Add(new SensorAlert(plugin.Id, device, AsusAlertKinds.CurrentImbalance, now, [Pin(highest), Pin(lowest)], _monitor.ImbalancePercentage));
                }
            }
        }
        var overcurrent = alerts.FirstOrDefault(alert => alert.Kind == AsusAlertKinds.WireOvercurrent)?.Pins.ToDictionary(pin => pin.Channel) ?? [];
        foreach (var metric in plugin.Metrics.Where(metric => metric.Definition.Metric == SensorMetric.OvercurrentDetected && metric.Mode != DebugMetricMode.Live))
        {
            var pin = metric.Definition.Channel!.Value;
            overcurrent.Remove(pin);

            if (metric is { Mode: DebugMetricMode.Fixed, Value: 1 } && currents.TryGetValue("Pin " + pin, out var current))
            {
                overcurrent[pin] = new AlertPin(pin, current);
            }
        }

        alerts.RemoveAll(alert => alert.Kind == AsusAlertKinds.WireOvercurrent);

        if (overcurrent.Count > 0)
        {
            alerts.Add(new SensorAlert(plugin.Id, device, AsusAlertKinds.WireOvercurrent, now, [.. overcurrent.Values]));
        }

        return [.. alerts];
    }

    private static AlertPin Pin(KeyValuePair<string, float> pin) => new(int.Parse(pin.Key.AsSpan(4), System.Globalization.CultureInfo.InvariantCulture), pin.Value);
}
