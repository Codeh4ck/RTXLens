using RTXLens.Plugins.GenericNvidia.Contracts;
using RTXLens.Plugins.Msi.Contracts;
using RTXLens.Plugins.WireView.Contracts;
using RTXLens.Plugins.Asus.Contracts;
using RTXLens.Contracts.Enums;
using RTXLens.Debugging.Models;

namespace RTXLens.Debugging.Catalog;

public static class DebugMetricCatalog
{
    public static DebugMetricDefinition[] For(string pluginId)
    {
        if (pluginId == NvidiaIdentity.Id)
        {
            return GenericNvidiaDebugMetrics.Create();
        }

        var metrics = new List<DebugMetricDefinition>();
        if (pluginId == MsiIdentity.Id)
        {
            for (var connector = 1; connector <= 2; connector++)
            {
                var id = connector.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var group = "PSU connector " + id;

                for (var pin = 1; pin <= 6; pin++)
                {
                    metrics.Add(new DebugMetricDefinition(group, $"P{pin} current", SensorComponent.PsuPin, SensorMetric.Current, connector == 1 ? 6 : 0, pin, id));
                }

                metrics.Add(new DebugMetricDefinition(group, "Connector current", SensorComponent.PsuConnector, SensorMetric.Current, connector == 1 ? 36 : 0,
                    ConnectorId: id));
            }

            metrics.AddRange([
                new DebugMetricDefinition("PSU", "Temperature", SensorComponent.Psu, SensorMetric.Temperature, 45),
                new DebugMetricDefinition("PSU", "Fan speed", SensorComponent.Psu, SensorMetric.FanSpeed, 1000, 1),
                new DebugMetricDefinition("PSU", "Requested fan duty", SensorComponent.Psu, SensorMetric.FanDuty, 50, 1),
                new DebugMetricDefinition("PSU", "Automatic fan mode", SensorComponent.Psu, SensorMetric.FanAutomatic, 1, 1)
            ]);

            return [.. metrics];
        }

        for (var pin = 1; pin <= 6; pin++)
        {
            metrics.Add(new DebugMetricDefinition($"Pin {pin}", "Current", SensorComponent.GpuPin, SensorMetric.Current, 6, pin));
            metrics.Add(new DebugMetricDefinition($"Pin {pin}", "Voltage", SensorComponent.GpuPin, SensorMetric.Voltage, 12, pin));
            metrics.Add(new DebugMetricDefinition($"Pin {pin}", "Power", SensorComponent.GpuPin, SensorMetric.Power, 72, pin));
        }

        metrics.AddRange([
            new DebugMetricDefinition("Connector", "Connector current", SensorComponent.GpuConnector, SensorMetric.Current, 36, 1),
            new DebugMetricDefinition("Connector", "Connector power", SensorComponent.GpuConnector, SensorMetric.Power, 432, 1),
            new DebugMetricDefinition("Connector", "Voltage", SensorComponent.GpuConnector, SensorMetric.Voltage, 12, 1)
        ]);
        
        if (pluginId == WireViewIdentity.Id)
        {
            metrics.AddRange([
                new DebugMetricDefinition("Connector", "Cable rating", SensorComponent.GpuConnector, SensorMetric.PowerCapability, 600, 1),
                new DebugMetricDefinition("Temperatures", "Onboard in", SensorComponent.MonitorBoard, SensorMetric.Temperature, 40, 1),
                new DebugMetricDefinition("Temperatures", "Onboard out", SensorComponent.MonitorBoard, SensorMetric.Temperature, 42, 2),
                new DebugMetricDefinition("Temperatures", "External probe 1", SensorComponent.ExternalProbe, SensorMetric.Temperature, 35, 1),
                new DebugMetricDefinition("Temperatures", "External probe 2", SensorComponent.ExternalProbe, SensorMetric.Temperature, 36, 2),
                new DebugMetricDefinition("Faults", "Active faults", SensorComponent.MonitorBoard, SensorMetric.FaultStatus, 0),
                new DebugMetricDefinition("Faults", "Fault log", SensorComponent.MonitorBoard, SensorMetric.FaultLog, 0)
            ]);
        }
        else if (pluginId == AsusIdentity.Id)
        {
            metrics.AddRange([
                new DebugMetricDefinition("GPU", "GPU board power", SensorComponent.GpuBoard, SensorMetric.Power, 450),
                new DebugMetricDefinition("GPU", "GPU usage", SensorComponent.GpuCore, SensorMetric.Utilization, 25),
                new DebugMetricDefinition("GPU", "Core clock", SensorComponent.GpuCore, SensorMetric.Clock, 2000),
                new DebugMetricDefinition("GPU", "Memory clock", SensorComponent.GpuMemory, SensorMetric.Clock, 10000),
                new DebugMetricDefinition("Temperatures", "Core temperature", SensorComponent.GpuCore, SensorMetric.Temperature, 60),
                new DebugMetricDefinition("Temperatures", "VRAM temperature", SensorComponent.GpuMemory, SensorMetric.Temperature, 65),
                new DebugMetricDefinition("Fans", "Fan 1", SensorComponent.GpuFan, SensorMetric.FanSpeed, 1200, 1),
                new DebugMetricDefinition("Fans", "Fan 2", SensorComponent.GpuFan, SensorMetric.FanSpeed, 1300, 2),
                new DebugMetricDefinition("Memory", "VRAM used (MB)", SensorComponent.GpuMemory, SensorMetric.MemoryUsed, 8192),
                new DebugMetricDefinition("Memory", "VRAM total (MB)", SensorComponent.GpuMemory, SensorMetric.MemoryTotal, 32768),
                new DebugMetricDefinition("Memory", "VRAM free (MB)", SensorComponent.GpuMemory, SensorMetric.MemoryFree, 24576),
                new DebugMetricDefinition("Memory", "VRAM usage", SensorComponent.GpuMemory, SensorMetric.Utilization, 25),
                new DebugMetricDefinition("Protection", "Imbalance percentage", SensorComponent.GpuConnector, SensorMetric.ImbalancePercentage, 0, 1),
                new DebugMetricDefinition("Protection", "Imbalance detected", SensorComponent.GpuConnector, SensorMetric.ImbalanceDetected, 0, 1)
            ]);

            for (var pin = 1; pin <= 6; pin++)
            {
                metrics.Add(new DebugMetricDefinition("Protection", $"P{pin} overcurrent detected", SensorComponent.GpuPin, SensorMetric.OvercurrentDetected, 0, pin));
            }
        }

        return [.. metrics];
    }
}