export type PairedComputer = {
  localAddress: string;
  remoteAddress: string;
  publicKeyFingerprint: string;
  token: string;
};

export type PairingCode = Omit<PairedComputer, 'token'> & {
  secret: string;
  expiresAt: number;
};

export type PairingReply = { status: number; body: string };
export type PairingRequest = (path: '/pairing' | '/pairing/complete', body: string) => Promise<PairingReply>;

type SensorReading = {
  identity: { key: string; pluginId: string; component: string; metric: string; channel: number | null };
  unit: string;
  value: number | null;
  quality: string;
};

type WarningState = { title: string; phase: string; episodeId: string };

export type SensorFrame = {
  sequence: number;
  timestamp: string;
  sensors: SensorReading[];
  warnings: WarningState[];
};

const secretPattern = /^[A-Za-z0-9_-]{43}$/;
const fingerprintPattern = /^[a-f0-9]{64}$/i;
const tokenPattern = /^[a-f0-9]{32}\.[A-Za-z0-9_-]{43}$/i;
const approvalIdPattern = /^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i;

function parseObject(json: string): Record<string, unknown> | null {
  try {
    return asObject(JSON.parse(json));
  } catch {
    return null;
  }
}

function asObject(value: unknown): Record<string, unknown> | null {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
    ? value as Record<string, unknown>
    : null;
}

function isIpv4(host: string): boolean {
  const parts = host.split('.');
  return parts.length === 4 && parts.every(part => /^(0|[1-9]\d{0,2})$/.test(part) && Number(part) <= 255);
}

function isHostName(host: string): boolean {
  const parts = host.split('.');
  return host.length <= 253 && parts.length >= 2
    && /^[a-z0-9-]*[a-z][a-z0-9-]*$/i.test(parts[parts.length - 1])
    && parts.every(part => /^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/i.test(part));
}

function readAddress(value: unknown, local: boolean): string | null {
  if (typeof value !== 'string') {
    return null;
  }

  const match = /^https:\/\/([a-z0-9.-]+):(\d{1,5})\/?$/i.exec(value);
  if (!match) {
    return null;
  }

  const [, host, portText] = match;
  const port = Number(portText);
  if (port < 1024 || port > 65535 || !(isIpv4(host) || (!local && isHostName(host)))) {
    return null;
  }

  return `https://${host.toLowerCase()}:${port}`;
}

function readComputer(value: Record<string, unknown>) {
  const localAddress = readAddress(value.localAddress, true);
  const remoteAddress = readAddress(value.remoteAddress, false);
  const fingerprint = value.publicKeyFingerprint;

  if (!localAddress || !remoteAddress || typeof fingerprint !== 'string' || !fingerprintPattern.test(fingerprint)) {
    return null;
  }

  return { localAddress, remoteAddress, publicKeyFingerprint: fingerprint.toLowerCase() };
}

export function decodePairingCode(json: string, now = Date.now()): PairingCode {
  const value = json.length <= 2048 ? parseObject(json) : null;
  if (!value || value.schemaVersion !== 1 || typeof value.secret !== 'string' || !secretPattern.test(value.secret)) {
    throw new Error('This is not an RTX Lens pairing code.');
  }

  const computer = readComputer(value);
  const expiresAt = typeof value.expiresAt === 'string' ? Date.parse(value.expiresAt) : NaN;
  if (!computer || !Number.isFinite(expiresAt)) {
    throw new Error('The RTX Lens pairing code is invalid.');
  }

  if (expiresAt <= now) {
    throw new Error('This code expired. Create a new code on your PC.');
  }

  return { ...computer, secret: value.secret, expiresAt };
}

export function decodeSavedComputer(json: string): PairedComputer | null {
  const value = parseObject(json);
  if (!value || typeof value.token !== 'string' || !tokenPattern.test(value.token)) {
    return null;
  }

  const computer = readComputer(value);
  return computer ? { ...computer, token: value.token } : null;
}

export async function requestPairing(
  code: PairingCode, deviceName: string, post: PairingRequest, waiting: () => void, signal: AbortSignal,
): Promise<PairedComputer | null> {
  if (signal.aborted) {
    return null;
  }

  const response = await post('/pairing', JSON.stringify({ invite: code.secret, deviceName }));
  if (signal.aborted) {
    return null;
  }

  const claim = parseObject(response.body);
  if (response.status !== 200 || !claim || typeof claim.id !== 'string' || !approvalIdPattern.test(claim.id)
    || typeof claim.secret !== 'string' || !secretPattern.test(claim.secret)) {
    throw new Error('The PC declined the code. Create a new one in Mobile Access.');
  }

  waiting();
  const body = JSON.stringify({ id: claim.id, secret: claim.secret });
  while (!signal.aborted && Date.now() < code.expiresAt) {
    const completed = await post('/pairing/complete', body);
    if (signal.aborted) {
      return null;
    }

    if (completed.status === 202) {
      await new Promise(resolve => setTimeout(resolve, 2000));
      continue;
    }

    const credential = parseObject(completed.body);
    if (completed.status !== 200 || typeof credential?.token !== 'string' || !tokenPattern.test(credential.token)) {
      throw new Error('Pairing was declined or expired. Create a new code on your PC.');
    }

    return {
      localAddress: code.localAddress,
      remoteAddress: code.remoteAddress,
      publicKeyFingerprint: code.publicKeyFingerprint,
      token: credential.token,
    };
  }

  if (signal.aborted) {
    return null;
  }

  throw new Error('The pairing request expired.');
}

function readSensor(value: unknown): SensorReading | null {
  const sensor = asObject(value);
  const identity = asObject(sensor?.identity);
  if (!sensor || !identity || typeof identity.key !== 'string' || typeof identity.pluginId !== 'string'
    || typeof identity.component !== 'string' || typeof identity.metric !== 'string'
    || (identity.channel != null && typeof identity.channel !== 'number')
    || typeof sensor.unit !== 'string' || typeof sensor.quality !== 'string'
    || (sensor.value !== null && (typeof sensor.value !== 'number' || !Number.isFinite(sensor.value)))) {
    return null;
  }

  return {
    identity: {
      key: identity.key,
      pluginId: identity.pluginId,
      component: identity.component,
      metric: identity.metric,
      channel: typeof identity.channel === 'number' ? identity.channel : null,
    },
    unit: sensor.unit,
    value: sensor.value,
    quality: sensor.quality,
  };
}

export function decodeFrame(json: string): SensorFrame | null {
  const snapshot = json.length <= 1024 * 1024 ? parseObject(json) : null;
  if (!snapshot || snapshot.schemaVersion !== 1 || typeof snapshot.sequence !== 'number'
    || !Number.isSafeInteger(snapshot.sequence) || snapshot.sequence < 0
    || typeof snapshot.timestamp !== 'string' || !Number.isFinite(Date.parse(snapshot.timestamp))
    || !Array.isArray(snapshot.sensors)) {
    return null;
  }

  const sensors = snapshot.sensors.map(readSensor).filter(sensor => sensor !== null);
  const warningStates = asObject(snapshot.warnings)?.states;
  const warnings = Array.isArray(warningStates)
    ? warningStates.flatMap(item => {
        const warning = asObject(item);
        return typeof warning?.title === 'string' && typeof warning.phase === 'string' && typeof warning.episodeId === 'string'
          ? [{ title: warning.title, phase: warning.phase, episodeId: warning.episodeId }]
          : [];
      })
    : [];

  return { sequence: snapshot.sequence, timestamp: snapshot.timestamp, sensors, warnings };
}
