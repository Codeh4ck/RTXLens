import { NetworkStateType } from 'expo-network';
import { useCallback, useEffect, useEffectEvent, useRef, useState } from 'react';

import {
  destinations, localProbeTimeout, retryDelay, selectConnectionIssue, sessionResult,
  type ConnectionIssue, type ConnectionProgress, type ConnectionStage, type Destination, type RouteChecks,
} from './connection';
import { decodeFrame, type PairedComputer, type SensorFrame } from './protocol';
import { transport } from './transport';

const firstFrameTimeout = 15_000;
const streamIdleTimeout = 45_000;
const initialProgress: ConnectionProgress = { stage: 'idle', checks: {} };

type Attempt = {
  id: string;
  destination: Destination;
  streamOpened: boolean;
  rejectedFrame: boolean;
  lastSequence: number;
};

export function usePcConnection(computer: PairedComputer | null, active: boolean, network: NetworkStateType | undefined,
  ready: boolean, onFrame: (frame: SensorFrame) => void) {
  const [retryVersion, setRetryVersion] = useState(0);
  const [run, setRun] = useState<{ computer: PairedComputer; retryVersion: number; progress: ConnectionProgress } | null>(null);
  const [snapshot, setSnapshot] = useState<{ token: string; frame: SensorFrame; destination: Destination } | null>(null);
  const progress = run?.progress ?? initialProgress;
  const revoked = useRef<string | null>(null);
  const previousNetwork = useRef(network);
  const nextStream = useRef(0);
  const receive = useEffectEvent(onFrame);
  const offline = network === NetworkStateType.NONE;

  const retry = useCallback(() => {
    revoked.current = null;
    setRetryVersion(value => value + 1);
  }, []);

  useEffect(() => {
    const previous = previousNetwork.current;
    previousNetwork.current = network;
    if (!computer || previous === undefined || previous === NetworkStateType.NONE || network === undefined || previous === network
      || offline || progress.stage === 'revoked') return;

    const keepRemoteConnection = progress.stage === 'live' && progress.route === 'remote' && network === NetworkStateType.WIFI;
    if (!keepRemoteConnection) retry();
  }, [computer, network, offline, progress.route, progress.stage, retry]);

  useEffect(() => {
    if (!transport || !computer || !ready || !active || offline || revoked.current === computer.token) return;

    const native = transport;
    const pairedComputer = computer;
    const { publicKeyFingerprint, token } = pairedComputer;
    const routes = destinations(computer);
    const checks: RouteChecks = {};
    let attempt: Attempt | null = null;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let disposed = false;
    let nextRoute = 0;
    let failedRounds = 0;
    let previousIssue: ConnectionIssue | null = null;

    function publish(stage: ConnectionStage, route?: Destination['route']) {
      setRun({ computer: pairedComputer, retryVersion, progress: { stage, route, checks: { ...checks } } });
    }

    function updateCheck(target: Destination, stage: ConnectionStage) {
      checks[target.route] = stage;
      // Keep recovery actions visible while automatic retries run in the background.
      publish(previousIssue ?? stage, target.route);
    }

    function stopAttempt() {
      clearTimeout(timer);
      const stopped = attempt;
      attempt = null;
      if (stopped) native.closeStream(stopped.id);
    }

    function fail(issue: ConnectionIssue) {
      if (!attempt) return;
      const { destination, lastSequence } = attempt;
      checks[destination.route] = issue;
      stopAttempt();

      if (issue === 'revoked') {
        revoked.current = token;
        publish(issue, destination.route);
        return;
      }

      if (lastSequence >= 0) {
        nextRoute = 0;
        failedRounds = 0;
      } else if (nextRoute >= routes.length) {
        previousIssue = selectConnectionIssue(checks);
        nextRoute = 0;
        publish(previousIssue);
        timer = setTimeout(() => void connectNext(), retryDelay(++failedRounds));
        return;
      }
      void connectNext();
    }

    async function connectNext() {
      const target = routes[nextRoute++];
      const current: Attempt = {
        id: String(++nextStream.current), destination: target, streamOpened: false, rejectedFrame: false, lastSequence: -1,
      };
      attempt = current;
      updateCheck(target, 'checking');
      const timeout = localProbeTimeout(target, routes.length, failedRounds);
      if (timeout !== undefined) timer = setTimeout(() => fail('unreachable'), timeout);

      try {
        const reply = await native.get(target.address, publicKeyFingerprint, '/session', token);
        if (disposed || attempt !== current) return;
        clearTimeout(timer);
        const result = sessionResult(reply);
        if (result !== 'connect') {
          fail(result === 'retry' ? 'unreachable' : result);
          return;
        }

        updateCheck(target, 'opening-stream');
        timer = setTimeout(() => fail('stream-unavailable'), firstFrameTimeout);
        native.openStream(current.id, target.address, publicKeyFingerprint, token);
      } catch {
        if (!disposed && attempt === current) {
          fail(checks[target.route] === 'opening-stream' ? 'stream-unavailable' : 'unreachable');
        }
      }
    }

    const states = native.addListener('onStreamState', event => {
      const current = attempt;
      if (!current || event.id !== current.id) return;
      if (event.reason === 'revoked') {
        fail('revoked');
      } else if (event.state !== 'connected') {
        fail(current.rejectedFrame && current.lastSequence < 0 ? 'incompatible' : 'stream-unavailable');
      } else {
        current.streamOpened = true;
        clearTimeout(timer);
        updateCheck(current.destination, 'waiting-for-readings');
        timer = setTimeout(() => fail(current.rejectedFrame ? 'incompatible' : 'no-readings'), firstFrameTimeout);
      }
    });
    const frames = native.addListener('onFrame', event => {
      const current = attempt;
      if (!current?.streamOpened || event.id !== current.id) return;
      const decoded = decodeFrame(event.json);
      if (!decoded) {
        current.rejectedFrame = true;
        return;
      }
      if (decoded.sequence <= current.lastSequence) return;

      clearTimeout(timer);
      timer = setTimeout(() => fail('no-readings'), streamIdleTimeout);
      if (current.lastSequence < 0) {
        failedRounds = 0;
        previousIssue = null;
        updateCheck(current.destination, 'live');
      }
      current.lastSequence = decoded.sequence;
      setSnapshot({ token, frame: decoded, destination: current.destination });
      receive(decoded);
    });

    void connectNext();
    return () => {
      disposed = true;
      states.remove();
      frames.remove();
      stopAttempt();
    };
  }, [active, computer, offline, ready, retryVersion]);

  const display: ConnectionProgress = !computer ? initialProgress
    : offline ? { stage: 'offline', checks: {} }
    : progress.stage === 'revoked' && run?.computer === computer && run.retryVersion === retryVersion ? progress
    : !active || run?.computer !== computer || run.retryVersion !== retryVersion
      ? { stage: 'checking', checks: {} } : progress;
  const currentSnapshot = snapshot?.token === computer?.token ? snapshot : null;
  return {
    progress: display,
    frame: currentSnapshot?.frame ?? null,
    destination: currentSnapshot?.destination ?? null,
    live: display.stage === 'live',
    retry,
  };
}
