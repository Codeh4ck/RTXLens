import type { Incident } from './incidentProtocol.ts';
import { AsusId, MsiId, WireViewId } from './pluginIds.ts';
import type { SensorFrame, SensorReading } from './protocol.ts';

export const demoAlerts = [
  { source: WireViewId, title: 'WireView board overtemperature', detail: 'Board In temperature reached 82 °C.', icon: 'temp' },
  { source: AsusId, title: 'ROG Astral GPU temperature', detail: 'Core temperature reached 88 °C.', icon: 'temp' },
  { source: MsiId, title: 'MSI connector pin overcurrent', detail: 'Connector 1, pin 3 reached 10.2 A.', icon: 'current' },
] as const;

export type DemoAlert = (typeof demoAlerts)[number];

export function demoFrame(now: number, incidents: Incident[]): SensorFrame {
  const sensors: SensorReading[] = [];
  const active = incidents.filter(item => item.endedAt === null);
  const triggered = (source: string) => active.some(item => item.source === source);
  const wave = Math.sin(now / 9000);
  const currents = Array.from({ length: 6 }, (_, i) =>
    i === 2 && triggered(MsiId) ? 10.2 : 5.4 + i * 0.14 + wave * 0.85);
  const amps = currents.reduce((sum, value) => sum + value, 0);
  const volts = 12.06;
  function add(pluginId: string, component: string, metric: string, unit: string, value: number,
    channel: number | null = null, connectorId: string | null = null) {
    const deviceId = 'demo-' + pluginId;
    const key = [pluginId, deviceId, connectorId, component, metric, channel].join('/');
    sensors.push({ identity: { key, pluginId, deviceId, component, metric, channel, connectorId }, unit, value, quality: 'Valid' });
    return key;
  }

  for (const source of [WireViewId, AsusId]) {
    add(source, 'GpuConnector', 'Current', 'Amperes', amps);
    add(source, 'GpuConnector', 'Voltage', 'Volts', volts);
    add(source, 'GpuConnector', 'Power', 'Watts', amps * volts);
    currents.forEach((current, i) => {
      add(source, 'GpuPin', 'Current', 'Amperes', current, i + 1);
      add(source, 'GpuPin', 'Voltage', 'Volts', volts, i + 1);
      add(source, 'GpuPin', 'Power', 'Watts', volts * current, i + 1);
    });
  }
  for (const connector of ['1', '2']) {
    const powered = connector === '1';
    add(MsiId, 'PsuConnector', 'Current', 'Amperes', powered ? amps : 0, null, connector);
    currents.forEach((current, i) => add(MsiId, 'PsuPin', 'Current', 'Amperes', powered ? current : 0, i + 1, connector));
  }
  add(WireViewId, 'MonitorBoard', 'Temperature', 'Celsius', triggered(WireViewId) ? 82 : 43 + wave, 1);
  add(WireViewId, 'MonitorBoard', 'Temperature', 'Celsius', 41 + wave, 2);
  add(WireViewId, 'ExternalProbe', 'Temperature', 'Celsius', 36 + wave, 1);
  add(WireViewId, 'ExternalProbe', 'Temperature', 'Celsius', 32 + wave, 2);
  add(WireViewId, 'MonitorBoard', 'FaultStatus', 'Count', triggered(WireViewId) ? 2 : 0);
  add(WireViewId, 'MonitorBoard', 'FaultLog', 'Count', incidents.some(item => item.source === WireViewId) ? 2 : 0);
  add(AsusId, 'GpuCore', 'Temperature', 'Celsius', triggered(AsusId) ? 88 : 63 + wave * 3);
  add(AsusId, 'GpuMemory', 'Temperature', 'Celsius', 68 + wave * 2);
  add(AsusId, 'GpuCore', 'Utilization', 'Percent', Math.round(80 + wave * 9));
  add(AsusId, 'GpuCore', 'Voltage', 'Volts', 0.96 + wave * 0.02);
  add(AsusId, 'GpuCore', 'Clock', 'Megahertz', Math.round(2750 + wave * 90));
  add(AsusId, 'GpuMemory', 'Clock', 'Megahertz', 14001);
  add(AsusId, 'GpuCrossbar', 'Clock', 'Megahertz', 2505);
  add(AsusId, 'GpuSystem', 'Clock', 'Megahertz', 2820);
  add(AsusId, 'GpuMemory', 'MemoryUsed', 'Megabytes', Math.round(13500 + wave * 950));
  add(AsusId, 'GpuMemory', 'MemoryTotal', 'Megabytes', 32768);
  for (const fan of [1, 2]) {
    add(AsusId, 'GpuFan', 'FanSpeed', 'Rpm', Math.round(1280 + wave * 90 + fan * 25), fan);
    add(AsusId, 'GpuFan', 'FanDuty', 'Percent', Math.round(44 + wave * 3), fan);
  }
  add(AsusId, 'GpuBoard', 'Power', 'Watts', amps * volts + 12);
  add(AsusId, 'GpuPowerLimits', 'EnforcedPowerLimit', 'Watts', 575);
  add(AsusId, 'GpuPerformance', 'PerformanceState', 'Count', 0);
  add(AsusId, 'GpuPerformance', 'SupportedClockReasons', 'Count', 37);
  add(AsusId, 'GpuPerformance', 'ActiveClockReasons', 'Count', triggered(AsusId) ? 32 : 0);
  add(AsusId, 'GpuPcie', 'LinkGeneration', 'Count', 5);
  add(AsusId, 'GpuPcie', 'MaximumSystemLinkGeneration', 'Count', 5);
  add(AsusId, 'GpuPcie', 'LinkWidth', 'Count', 16);
  add(AsusId, 'GpuPcie', 'MaximumLinkWidth', 'Count', 16);
  add(AsusId, 'GpuPcie', 'ReceiveThroughput', 'BytesPerSecond', 17000000 + wave * 3000000);
  add(AsusId, 'GpuPcie', 'TransmitThroughput', 'BytesPerSecond', 6000000 + wave * 1000000);

  return {
    sequence: now, timestamp: new Date(now).toISOString(), sensors, reportingSources: 3,
    connectorPowerKey: sensors.find(r => r.identity.pluginId === WireViewId && r.identity.component === 'GpuConnector' && r.identity.metric === 'Power')!.identity.key,
    warnings: active.map(item => ({ title: item.title, detail: item.detail, pluginId: item.source, phase: 'Active',
      icon: demoAlerts.find(alert => alert.source === item.source)!.icon, episodeId: item.id, startedAt: item.startedAt, notificationSequence: 0 })),
    warningInstance: 'local-demo', notificationsEnabled: false, simulationWarnings: [], safeguards: [],
    pinLimits: [WireViewId, AsusId, MsiId].map(pluginId => ({ pluginId, deviceId: 'demo-' + pluginId, overcurrentAmperes: 9.2 })),
  };
}

export function triggerDemoAlert(incidents: Incident[], alert: DemoAlert, now: number): Incident[] {
  if (incidents.some(item => item.source === alert.source && !item.endedAt)) return incidents;
  const startedAt = new Date(now).toISOString();
  const incident: Incident = {
    id: alert.source + '-' + now, kind: 'warning', title: alert.title, detail: alert.detail, source: alert.source,
    status: 'Active', startedAt, endedAt: null, capturedAt: startedAt, deviceId: 'demo-' + alert.source,
    connector: alert.source === MsiId ? 1 : null, delayMs: null, deliveryStatus: null, readings: [], steps: [],
  };
  const next = [incident, ...incidents];
  incident.readings = demoFrame(now, next).sensors.filter(reading => reading.identity.pluginId === alert.source);
  return next;
}

export function clearDemoAlerts(incidents: Incident[], now: number) {
  const endedAt = new Date(now).toISOString();
  return incidents.map(item => item.endedAt ? item : { ...item, status: 'Cleared', endedAt });
}
