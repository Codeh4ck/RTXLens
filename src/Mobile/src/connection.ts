import type { PairedComputer } from './protocol';

export type ConnectionIssue = 'unreachable' | 'changed-key' | 'revoked' | 'local-network-denied' | 'stream-unavailable' | 'no-readings' | 'incompatible';

export type ConnectionStage = ConnectionIssue | 'idle' | 'offline' | 'checking' | 'opening-stream' | 'waiting-for-readings' | 'live';

export type RouteChecks = Partial<Record<Destination['route'], ConnectionStage>>;

export type ConnectionProgress = { stage: ConnectionStage; route?: Destination['route']; checks: RouteChecks };

export type Destination = { route: 'local' | 'remote'; address: string };

export function destinations(computer: PairedComputer): Destination[] {
  const local: Destination = { route: 'local', address: computer.localAddress };
  const remote: Destination = { route: 'remote', address: computer.remoteAddress };

  return local.address === remote.address ? [local] : [local, remote];
}

export function localSwitchAddress(computer: PairedComputer, destination: Destination | null): string | null {
  return destination?.route === 'remote' && destination.address !== computer.localAddress ? computer.localAddress : null;
}

export function localProbeTimeout(target: Destination, routeCount: number, failedRounds: number): number | undefined {
  return target.route === 'local' && routeCount > 1 && failedRounds === 0 ? 3_000 : undefined;
}

export function selectConnectionIssue(checks: RouteChecks): ConnectionIssue {
  const priority: ConnectionIssue[] = [
    'revoked', 'incompatible', 'no-readings', 'stream-unavailable', 'local-network-denied', 'changed-key',
  ];

  return priority.find(issue => checks.local === issue || checks.remote === issue) ?? 'unreachable';
}

export function retryDelay(failures: number) {
  return Math.min(30_000, 1_000 * 2 ** Math.min(5, Math.max(0, failures - 1)));
}

export function sessionResult(result: { status: number; keyMismatch?: boolean; localNetworkDenied?: boolean }) {
  if (result.localNetworkDenied) return 'local-network-denied';
  if (result.keyMismatch) return 'changed-key';
  if (result.status === 401) return 'revoked';
  if (result.status === 204 || result.status === 404) return 'connect';

  return 'retry';
}
