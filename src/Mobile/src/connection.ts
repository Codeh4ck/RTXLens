import type { PairedComputer } from './protocol';

export type StopReason = 'revoked' | 'changed-key';

export type Destination = { route: 'local' | 'remote'; address: string };

export function destinations(computer: PairedComputer): Destination[] {
  const local: Destination = { route: 'local', address: computer.localAddress };
  const remote: Destination = { route: 'remote', address: computer.remoteAddress };
  return local.address === remote.address ? [local] : [local, remote];
}

export function localSwitchAddress(computer: PairedComputer, destination: Destination | null): string | null {
  return destination?.route === 'remote' && destination.address !== computer.localAddress ? computer.localAddress : null;
}

export function retryDelay(failures: number) {
  return Math.min(30_000, 1_000 * 2 ** Math.min(5, Math.max(0, failures - 1)));
}

export function sessionResult(result: { status: number; keyMismatch?: boolean }) {
  if (result.keyMismatch) return 'changed-key';
  if (result.status === 401) return 'revoked';
  if (result.status === 204 || result.status === 404) return 'connect';
  return 'retry';
}
