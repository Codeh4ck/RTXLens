import { pluginNames } from './pluginNames.ts';
import { AsusId, MsiId, NvidiaId, WireViewId } from './pluginIds.ts';
import { homePanelKey, type HomePinPanel, type TemperatureUnit } from './mobilePreferences.ts';
import type { SensorReading } from './protocol';

const hardwareOrder = new Map<string, number>(
  [WireViewId, AsusId, MsiId, NvidiaId].map((id, index) => [id, index]),
);

export const pinMetrics = ['Current', 'Power', 'Voltage'] as const;
export type PinMetric = typeof pinMetrics[number];

export type MetricGroup = {
  key: string;
  name: string;
  source: string;
  readings: SensorReading[];
};

export function sourceName(id: string) {
  return Object.hasOwn(pluginNames, id) ? pluginNames[id] : id ? words(id) : 'RTX Lens';
}

export function isPin(reading: SensorReading) {
  return reading.identity.component === 'GpuPin' || reading.identity.component === 'PsuPin';
}

export function groupMetrics(sensors: SensorReading[], kind: 'power' | 'gpu'): MetricGroup[] {
  const groups = new Map<string, MetricGroup>();
  for (const sensor of sensors) {
    const { pluginId, deviceId, component } = sensor.identity;
    const isPower = isPin(sensor) || ['GpuConnector', 'PsuConnector', 'MonitorBoard', 'ExternalProbe', 'Psu'].includes(component);
    if (isPower !== (kind === 'power')) {
      continue;
    }

    const key = JSON.stringify([pluginId, deviceId]);
    const existing = groups.get(key);
    if (existing) {
      existing.readings.push(sensor);
    } else {
      groups.set(key, { key, name: sourceName(pluginId), source: pluginId, readings: [sensor] });
    }
  }

  const labels = new Map<string, number>();
  return Array.from(groups.values())
    .filter(group => group.readings.some(reading => reading.quality === 'Valid' && reading.value !== null))
    .sort((left, right) => left.name.localeCompare(right.name) || left.key.localeCompare(right.key))
    .map(group => {
      const index = (labels.get(group.name) ?? 0) + 1;
      labels.set(group.name, index);
      return index === 1 ? group : { ...group, name: `${group.name} ${index}` };
    });
}

export function hardwareTabGroups(sensors: SensorReading[], kind: 'power' | 'gpu') {
  const groups = groupMetrics(sensors, kind).sort((left, right) =>
    (hardwareOrder.get(left.source) ?? hardwareOrder.size) - (hardwareOrder.get(right.source) ?? hardwareOrder.size));
  if (kind !== 'power') return groups;

  const astralReadings = groups.filter(group => group.source === AsusId).flatMap(group => group.readings)
    .filter(reading => reading.quality === 'Valid' && reading.value !== null);
  return groups.filter(group => group.source !== NvidiaId || group.readings.some(({ identity, quality, value }) =>
    quality === 'Valid' && value !== null && !astralReadings.some(({ identity: other }) =>
      other.deviceId === identity.deviceId && other.component === identity.component && other.metric === identity.metric
      && other.channel === identity.channel && other.connectorId === identity.connectorId)));
}

// NVIDIA and Astral use the same adapter ID from the shared hardware reader.
export function gpuDeviceGroups(sensors: SensorReading[]): MetricGroup[] {
  const devices = new Map<string, { group: MetricGroup; metrics: Map<string, SensorReading> }>();
  for (const group of groupMetrics(sensors, 'gpu')) {
    const sharedAdapter = group.source === AsusId || group.source === NvidiaId;
    const key = sharedAdapter ? JSON.stringify(['gpu', group.readings[0].identity.deviceId]) : group.key;
    const device = devices.get(key) ?? { group, metrics: new Map<string, SensorReading>() };
    if (group.source === NvidiaId) device.group = group;
    for (const reading of group.readings) {
      const { component, metric, channel, connectorId } = reading.identity;
      const slot = JSON.stringify([component, metric, channel, connectorId]);
      const previous = device.metrics.get(slot);
      const valid = reading.quality === 'Valid' && reading.value !== null;
      if (!previous || (valid && (previous.quality !== 'Valid' || previous.value === null || group.source === NvidiaId))) {
        device.metrics.set(slot, reading);
      }
    }
    devices.set(key, device);
  }
  return Array.from(devices, ([key, { group, metrics }]) => ({ ...group, key, readings: [...metrics.values()] }));
}

export function gpuConnectorPower(sensors: SensorReading[], selectedKey: string | null | undefined, deviceId: string, onlyGpu: boolean) {
  const connectors = sensors.filter(({ identity }) => identity.metric === 'Power'
    && (identity.component === 'GpuConnector' || identity.component === 'PsuConnector'));
  const sameAdapter = (r: SensorReading) => (r.identity.pluginId === NvidiaId || r.identity.pluginId === AsusId)
    && r.identity.deviceId === deviceId;
  if (selectedKey === undefined) {
    const local = connectors.filter(sameAdapter);
    return local.find(r => r.quality === 'Valid' && r.value !== null && r.identity.pluginId === NvidiaId)
      ?? local.find(r => r.quality === 'Valid' && r.value !== null);
  }
  const selected = connectors.find(r => r.identity.key === selectedKey);
  if (!selected) return undefined;
  // External meters have no GPU adapter ID. A global selection is ambiguous with multiple GPUs.
  if (selected.identity.pluginId === AsusId || selected.identity.pluginId === NvidiaId) return sameAdapter(selected) ? selected : undefined;
  return onlyGpu ? selected : undefined;
}

export function findMetric(sensors: SensorReading[], component: string, metric: string, channel?: number) {
  return sensors.find(sensor => sensor.identity.component === component && sensor.identity.metric === metric
    && (channel === undefined || channel === sensor.identity.channel) && sensor.quality === 'Valid' && sensor.value !== null);
}

export function powerConnectorGroups(group: MetricGroup, hideIdleMsiConnectors: boolean) {
  const byConnector = new Map<string | null, SensorReading[]>();
  for (const reading of group.readings) {
    const id = reading.identity.connectorId;
    const existing = byConnector.get(id);
    if (existing) existing.push(reading);
    else byConnector.set(id, [reading]);
  }

  const connectors = [...byConnector];
  return hideIdleMsiConnectors && group.source === MsiId
    ? connectors.filter(([id, readings]) => id === null || findMetric(readings, 'PsuConnector', 'Current')?.value !== 0)
    : connectors;
}

export function pinCurrentSummary(readings: SensorReading[]) {
  let high: { channel: number | null; value: number } | undefined;
  let low: typeof high;
  let count = 0;

  for (const { identity, value, quality } of readings) {
    if (identity.metric !== 'Current' || quality !== 'Valid' || value === null || !Number.isFinite(value)) continue;

    const pin = { channel: identity.channel, value };
    if (!high || value > high.value) high = pin;
    if (!low || value < low.value) low = pin;
    count++;
  }

  return count > 1 && high && low ? { delta: high.value - low.value, high, low } : null;
}

export function pinPanelTitle(panel: HomePinPanel, deviceName = sourceName(panel.pluginId)) {
  return panel.connectorId === null ? `${deviceName} pins` : `${deviceName} pins · Connector ${panel.connectorId}`;
}

export function pinPanelSources(groups: MetricGroup[]) {
  const sources = new Map<string, { panel: HomePinPanel; title: string; pins: SensorReading[] }>();
  for (const group of groups) {
    for (const [connectorId, readings] of powerConnectorGroups(group, false)) {
      const pins = readings.filter(reading => isPin(reading) && pinMetrics.some(metric => metric === reading.identity.metric));
      if (pins.length === 0) continue;

      const { pluginId, deviceId } = pins[0].identity;
      const panel: HomePinPanel = { kind: 'pins', pluginId, deviceId, connectorId };
      sources.set(homePanelKey(panel), { panel, title: pinPanelTitle(panel, group.name), pins });
    }
  }
  return sources;
}

const labels: Record<string, string> = {
  ActiveClockReasons: 'Active boost limits', DefaultPowerLimit: 'Default GPU power limit', EnforcedPowerLimit: 'Enforced GPU power limit',
  FaultLog: 'Fault Log Status', FaultStatus: 'Fault Status', LinkGeneration: 'PCIe generation', MaximumGpuLinkGeneration: 'GPU PCIe Gen',
  MaximumSystemLinkGeneration: 'Max PCIe generation', MaximumLinkSpeed: 'Max PCIe link speed', MaximumLinkWidth: 'Max PCIe link width',
  LinkSpeed: 'PCIe Link Speed', LinkWidth: 'PCIe link width', MemoryUsed: 'VRAM usage', MemoryTotal: 'Total VRAM', MemoryFree: 'Free VRAM',
  PerformanceState: 'Current P-state', PowerCapability: 'Cable rating', ReceiveThroughput: 'PCIe RX', TransmitThroughput: 'PCIe TX',
  ReplayCount: 'PCIe replays', ReplayRolloverCount: 'PCIe replay rollovers', SupportedPerformanceStates: 'Supported P-states',
};

const gpuClockLabels = new Map([
  ['GpuCore', 'Core clock'], ['GpuMemory', 'Memory clock'],
  ['GpuCrossbar', 'Crossbar clock'], ['GpuSystem', 'System clock'],
]);

export function isGpuPowerReading({ identity }: SensorReading) {
  return identity.component === 'GpuPowerLimits' || (identity.component === 'GpuBoard' && identity.metric === 'Power');
}

type GpuStatusRow = { reading: SensorReading; label?: string; value?: string };

// The GPU page passes readings for a single selected device.
export function gpuStatusRows(readings: SensorReading[], format = formatReading): GpuStatusRow[] {
  const clocks = new Map<string, GpuStatusRow>();
  const other: GpuStatusRow[] = [];
  const fans = new Map<number, { reading: SensorReading; speed?: SensorReading; duty?: SensorReading }>();
  const memoryTotal = findMetric(readings, 'GpuMemory', 'MemoryTotal');
  for (const reading of readings) {
    const { component, metric, channel } = reading.identity;
    if (isGpuPowerReading(reading) || component === 'GpuPcie' || component === 'GpuPerformance'
      || metric === 'MemoryTotal' || metric === 'MemoryFree') continue;
    if (metric === 'Clock' && gpuClockLabels.has(component)) {
      clocks.set(component, { reading });
    } else if (component === 'GpuFan' && (metric === 'FanSpeed' || metric === 'FanDuty')) {
      const index = channel ?? 1;
      const fan = fans.get(index) ?? { reading };
      if (metric === 'FanSpeed') fan.speed = reading;
      else fan.duty = reading;
      fans.set(index, fan);
    } else if (metric === 'MemoryUsed' && memoryTotal && reading.quality === 'Valid' && reading.value !== null) {
      const used = format(reading);
      const total = format(memoryTotal);
      const unit = total.slice(total.lastIndexOf(' '));
      other.push({ reading, value: `${used.endsWith(unit) ? used.slice(0, -unit.length) : used} / ${total}` });
    } else {
      other.push({ reading });
    }
  }

  const rows: GpuStatusRow[] = [];
  for (const component of gpuClockLabels.keys()) {
    const clock = clocks.get(component);
    if (clock) rows.push(clock);
  }
  rows.push(...other);
  for (const [index, { reading, speed, duty }] of [...fans].sort(([a], [b]) => a - b)) {
    const rpm = format(speed);
    const hasDuty = duty?.quality === 'Valid' && duty.value !== null;
    let value = rpm;
    if (hasDuty) {
      const percent = format(duty);
      value = speed?.quality === 'Valid' && speed.value !== null ? `${rpm} (${percent})` : `${percent} duty`;
    }
    rows.push({ reading, label: `Fan ${index}`, value });
  }
  return rows;
}

export function metricLabel({ identity }: SensorReading) {
  const { component, metric, channel } = identity;
  if (metric === 'Clock') return gpuClockLabels.get(component) ?? words(metric);
  if (metric === 'Temperature') {
    if (component === 'GpuCore') return 'Core temperature';
    if (component === 'GpuMemory') return 'VRAM temperature';
    if (component === 'GpuHotspot') return 'Hotspot temperature';
    if (component === 'MonitorBoard') return channel === 1 ? 'Board In temperature' : 'Board Out temperature';
    if (component === 'ExternalProbe') return channel == null ? 'External temperature' : `Ext. ${channel} temperature`;
    if (component === 'Psu') return 'PSU temperature';
  }
  if (component === 'GpuCore') {
    if (metric === 'Voltage') return 'Core voltage';
    if (metric === 'Utilization') return 'GPU usage';
  }
  if (component === 'GpuMemory') {
    if (metric === 'Utilization') return 'Memory usage';
  }
  if (component === 'GpuFan' || component === 'Psu') {
    const fan = component === 'Psu' ? 'PSU fan' : `Fan ${channel ?? 1}`;
    if (metric === 'FanSpeed') return fan;
    if (metric === 'FanDuty') return `${fan} duty`;
    if (metric === 'FanAutomatic') return `${fan} mode`;
  }
  if (component === 'GpuBoard' && metric === 'Power') return 'GPU board power';
  if (component === 'GpuConnector' || component === 'PsuConnector') {
    if (metric === 'Power') return 'Connector power';
    if (metric === 'Current') return 'Total current';
  }
  return Object.hasOwn(labels, metric) ? labels[metric] : words(metric);
}

const formats: Record<string, [string, number]> = {
  Amperes: ['A', 2], BytesPerSecond: ['B/s', 0], Celsius: ['°C', 1], Count: ['', 0],
  MegabitsPerSecond: ['Mb/s', 0], Megabytes: ['MB', 0], Megahertz: ['MHz', 0],
  Percent: ['%', 0], Rpm: ['RPM', 0], Seconds: ['s', 2], Volts: ['V', 2], Watts: ['W', 1],
};

const clockReasons = [
  'GPU idle', 'Application clocks', 'Software power cap', 'Hardware slowdown', 'Sync Boost', 'Software thermal slowdown',
  'Hardware thermal slowdown', 'Hardware power brake', 'Display clocks', 'Board limit', 'Reliability',
];

const faultNames = ['Overtemperature', 'Overcurrent', 'Wire overcurrent', 'Overpower', 'Current imbalance'];

export function wireViewFaults(readings: SensorReading[]) {
  const active = findMetric(readings, 'MonitorBoard', 'FaultStatus')?.value;
  const logged = findMetric(readings, 'MonitorBoard', 'FaultLog')?.value;
  return [...faultNames.map((name, index) => ({ name, mask: 2 << index })),
    { name: 'Unrecognized device fault', mask: 0xffff & ~0x3e }].flatMap(({ name, mask }) => {
      const isActive = ((active ?? 0) & mask) !== 0;
      const isLogged = ((logged ?? 0) & mask) !== 0;
      return isActive || isLogged ? [{ name, status: isActive && isLogged ? 'Active / Logged' : isActive ? 'Active' : 'Logged' }] : [];
    });
}

export function boostLimitRows(readings: SensorReading[]) {
  const active = findMetric(readings, 'GpuPerformance', 'ActiveClockReasons')?.value;
  const supported = findMetric(readings, 'GpuPerformance', 'SupportedClockReasons')?.value;
  const mask = (1 << clockReasons.length) - 1;
  const rows = clockReasons.flatMap((name, index) => {
    const flag = 1 << index;
    const seconds = findMetric(readings, 'GpuPerformance', 'ClockLimitDuration', flag)?.value ?? null;
    const isActive = active == null ? null : (active & flag) !== 0;
    return isActive || ((supported ?? 0) & flag) !== 0 || seconds !== null ? [{ name, active: isActive, seconds }] : [];
  });
  if ((active ?? 0) > mask || (supported ?? 0) > mask) {
    rows.push({ name: 'Unknown boost limit', active: active == null ? null : active > mask, seconds: null });
  }
  return rows;
}

export function formatReading(sensor?: SensorReading, temperatureUnit: TemperatureUnit = 'celsius', useGrouping = false) {
  if (!sensor || sensor.value === null || sensor.quality !== 'Valid') {
    return 'Unavailable';
  }

  const { identity: { component, metric }, unit, value } = sensor;
  if (metric === 'FaultStatus' || metric === 'FaultLog') {
    return value === 0 ? 'OK' : metric === 'FaultStatus' ? 'Fault detected' : 'Faults logged';
  }
  if (metric === 'PerformanceState') return `P${value}`;
  if (metric === 'SupportedPerformanceStates') {
    return Array.from({ length: 16 }, (_, index) => index).filter(index => value & (1 << index))
      .map(index => `P${index}`).join(', ') || 'None reported';
  }
  if (metric === 'ActiveClockReasons') {
    return clockReasons.filter((_, index) => value & (1 << index)).join(', ') || (value ? 'Unknown boost limit' : 'None active');
  }
  if (metric === 'FanAutomatic') return value ? 'Automatic' : 'Manual';
  if (unit === 'Boolean') return value ? 'Yes' : 'No';
  if (component === 'GpuCore' && metric === 'Voltage' && unit === 'Volts') return `${number(value * 1000, 1, useGrouping)} mV`;
  if (unit === 'Megabytes' && value >= 1024) return `${number(value / 1024, 2, useGrouping)} GiB`;
  if (unit === 'Celsius' && temperatureUnit === 'fahrenheit') return `${number(value * 9 / 5 + 32, 1, useGrouping)} °F`;
  if (unit === 'BytesPerSecond' && value >= 1024) {
    const exponent = Math.min(3, Math.floor(Math.log(value) / Math.log(1024)));
    return `${number(value / 1024 ** exponent, 2, useGrouping)} ${['B/s', 'KiB/s', 'MiB/s', 'GiB/s'][exponent]}`;
  }

  const [suffix, digits] = Object.hasOwn(formats, unit) ? formats[unit] : ['', 2];
  return suffix === '%' ? `${number(value, digits, useGrouping)}%` : [number(value, digits, useGrouping), suffix].filter(Boolean).join(' ');
}

export function number(value: number, digits: number, useGrouping = false, locale?: string) {
  return value.toLocaleString(locale, { maximumFractionDigits: digits, useGrouping });
}

function words(value: string) {
  return value.replace(/([a-z])([A-Z])/g, '$1 $2').replaceAll('-', ' ');
}
