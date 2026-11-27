import { asObject, readSensor, type SensorReading } from './protocol.ts';

export type IncidentSummary = {
  id: string;
  kind: 'warning' | 'safeguard';
  title: string;
  source: string;
  status: string;
  startedAt: string;
  endedAt: string | null;
  connector: number | null;
};

export type Incident = IncidentSummary & {
  detail: string;
  deviceId: string | null;
  delayMs: number | null;
  deliveryStatus: string | null;
  capturedAt: string | null;
  readings: SensorReading[];
  steps: { name: string; result: string; timestamp: string | null; exitCode: number | null }[];
};

export type IncidentRef = Pick<IncidentSummary, 'id' | 'kind'>;

const idPattern = /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i;
const cursorPattern = /^\d{1,19}\.[a-f\d]{32}\.(warning|safeguard)$/i;

function date(value: unknown) {
  return typeof value === 'string' && Number.isFinite(Date.parse(value)) ? value : null;
}

function integer(value: unknown) {
  return typeof value === 'number' && Number.isSafeInteger(value) ? value : null;
}

function summary(value: unknown): IncidentSummary | null {
  const record = asObject(value);
  const startedAt = date(record?.startedAt);

  if (!record || typeof record.id !== 'string' || !idPattern.test(record.id) || !startedAt
    || (record.kind !== 'warning' && record.kind !== 'safeguard') || typeof record.title !== 'string'
    || typeof record.source !== 'string' || typeof record.status !== 'string') {
    return null;
  }

  return {
    id: record.id, kind: record.kind, title: record.title, source: record.source, status: record.status,
    startedAt, endedAt: date(record.endedAt), connector: integer(record.connector),
  };
}

export function decodeIncidentPage(json: string) {
  const page = asObject(JSON.parse(json));

  if (!Array.isArray(page?.items) || page.items.length > 25 || (page.next !== null && (typeof page.next !== 'string' || !cursorPattern.test(page.next)))) {
    throw new Error('The PC returned an invalid history page.');
  }

  return { items: page.items.map(summary).filter(item => item !== null), next: page.next };
}

export function warningNotificationIncident(data: unknown): IncidentRef | null {
  const record = asObject(data);

  return record?.screen === 'warnings' && (record.kind === 'warning' || record.kind === 'safeguard')
    && typeof record.incidentId === 'string' && idPattern.test(record.incidentId)
    ? { id: record.incidentId.toLowerCase(), kind: record.kind } : null;
}

export function decodeIncident(json: string, expected: IncidentRef): Incident {
  const record = asObject(JSON.parse(json));
  const base = summary(record);

  if (!record || !base || base.id !== expected.id || base.kind !== expected.kind) {
    throw new Error('The PC returned an invalid incident.');
  }

  const steps = Array.isArray(record.steps) ? record.steps.slice(0, 128).flatMap(value => {
    const step = asObject(value);

    return typeof step?.name === 'string' && typeof step.result === 'string'
      ? [{ name: step.name, result: step.result, timestamp: date(step.timestamp), exitCode: integer(step.exitCode) }] : [];
  }) : [];
  
  return {
    ...base,
    detail: typeof record.detail === 'string' ? record.detail : '',
    deviceId: typeof record.deviceId === 'string' && record.deviceId.length > 0 ? record.deviceId : null,
    deliveryStatus: typeof record.deliveryStatus === 'string' ? record.deliveryStatus : null,
    delayMs: integer(record.delayMs), capturedAt: date(record.capturedAt),
    readings: Array.isArray(record.readings) ? record.readings.slice(0, 512).map(readSensor).filter(reading => reading !== null) : [],
    steps,
  };
}
