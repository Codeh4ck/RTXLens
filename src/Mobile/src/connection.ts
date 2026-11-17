import type { PairedComputer } from './protocol';

export type AddressRoute = 'local' | 'remote';
export type Route = 'auto' | AddressRoute;
export type StopReason = 'revoked' | 'changed-key';

export type Destination = { route: AddressRoute; address: string };

export function destinations(computer: PairedComputer, route: Route, cellular: boolean): Destination[] {
  const local: Destination = { route: 'local', address: computer.localAddress };
  const remote: Destination = { route: 'remote', address: computer.remoteAddress };
  if (route !== 'auto') {
    return [route === 'local' ? local : remote];
  }
  if (local.address === remote.address) {
    return [cellular ? remote : local];
  }
  return cellular ? [remote, local] : [local, remote];
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
