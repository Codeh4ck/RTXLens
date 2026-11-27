import { useEffect, useRef, useState } from 'react';
import { Text, View } from 'react-native';

import { mobileProtocol } from './mobileProtocol.ts';
import { AsusId, NvidiaId } from './pluginIds.ts';
import type { PairedComputer, WarningState } from './protocol';
import { transport } from './transport';
import { Button, useStyles } from './ui';

type Availability = {
  managedOnDesktop: boolean; enableShutdown: boolean; enableLowPower: boolean; gpuId?: string | null; powerMilliwatts?: number | null;
};
type Action = 'shutdown' | 'low-power';
type CommandResult = { state: string; milliwatts?: number; message?: string };

const watts = (milliwatts: number) => `${milliwatts / 1000} W`;

export function useWarningActions(computer: PairedComputer | null, address?: string | null, activeWarningEpisodes?: string) {
  const [snapshot, setSnapshot] = useState<{ key: string; availability?: Availability }>();
  const [revision, setRevision] = useState(0);
  const key = computer && address && activeWarningEpisodes ? `${computer.token}|${address}` : null;
  const client = transport;

  useEffect(() => {
    if (typeof client?.postWarningAction !== 'function' || !computer || !address || !key) return;
    let current = true;
    let pending = false;

    const refresh = async () => {
      if (pending) return;
      pending = true;
      try {
        const reply = await client.get(address, computer.publicKeyFingerprint, mobileProtocol.WarningActions, computer.token);
        const options: Availability | null = reply.status === 200 ? JSON.parse(reply.body) : null;
        if (current) {
          const availability = options?.managedOnDesktop === true && typeof options.enableShutdown === 'boolean'
            && typeof options.enableLowPower === 'boolean' ? options : undefined;
          setSnapshot({ key, availability });
        }
      } catch {
        if (current) setSnapshot({ key });
      } finally {
        pending = false;
      }
    };

    void refresh();
    const timer = setInterval(() => void refresh(), 10_000);
    return () => {
      current = false;
      clearInterval(timer);
    };
  }, [client, computer, address, key, activeWarningEpisodes, revision]);

  async function run(instanceId: string, episodeId: string, action: Action, statusOnly = false): Promise<CommandResult> {
    if (typeof client?.postWarningAction !== 'function' || !computer || !address)
      throw new Error('Connect to the PC first.');
    const reply = await client.postWarningAction(address, computer.publicKeyFingerprint, computer.token, mobileProtocol.RunWarningAction,
      JSON.stringify({ instanceId, episodeId, action, statusOnly }));
    if (reply.status === 403) {
      setRevision(value => value + 1);
      throw new Error('This phone is no longer allowed to run that action.');
    }
    if (reply.status !== 200)
      throw new Error('The PC did not accept the warning action.');
    return JSON.parse(reply.body) as CommandResult;
  }

  return { availability: key && snapshot?.key === key ? snapshot.availability : undefined, run };
}

export type WarningActions = ReturnType<typeof useWarningActions>;

export function ActiveWarningActions({ warning, instanceId, actions }: { warning: WarningState; instanceId: string; actions: WarningActions }) {
  const styles = useStyles();
  const [busy, setBusy] = useState<Action>();
  const [shutdown, setShutdown] = useState<string>();
  const [power, setPower] = useState<CommandResult>();
  const commandPending = useRef(false);
  const settings = actions.availability;
  const matchesGpu = warning.pluginId !== AsusId && warning.pluginId !== NvidiaId || warning.deviceId === settings?.gpuId;
  const limit = settings?.enableLowPower && matchesGpu && settings.gpuId ? settings.powerMilliwatts : undefined;
  if (!settings?.enableShutdown && limit == null) return null;

  async function run(action: Action) {
    if (commandPending.current) return;
    commandPending.current = true;
    setBusy(action);
    try {
      if (action === 'shutdown') {
        const result = await actions.run(instanceId, warning.episodeId, action);
        setShutdown(result.state === 'requested' ? 'Shutdown requested' : result.message ?? 'Shutdown failed.');
      } else {
        let result = await actions.run(instanceId, warning.episodeId, action, power?.state === 'requested');
        setPower(result);
        for (let attempt = 0; result.state === 'requested' && attempt < 6; attempt++) {
          await new Promise<void>(resolve => setTimeout(resolve, 750));
          result = await actions.run(instanceId, warning.episodeId, action, true);
          setPower(result);
        }
      }
    } catch (error) {
      const message = error instanceof Error && error.message.startsWith('This phone') ? error.message
        : action === 'shutdown' ? 'The PC did not confirm shutdown. Check its connection.'
          : 'Could not confirm the power limit. Check the PC’s connection.';
      if (action === 'shutdown') setShutdown(message);
      else setPower({ state: 'unknown', message });
    } finally {
      setBusy(undefined);
      commandPending.current = false;
    }
  }

  return <View>
    <View style={styles.actions}>
      {settings?.enableShutdown && <Button icon="warnings" label="Shut down PC now" variant="danger"
        disabled={busy !== undefined || shutdown === 'Shutdown requested'} onPress={() => void run('shutdown')} />}
      {limit != null && <Button icon="power" label={power?.state === 'requested' ? 'Check power limit' : `Limit GPU to ${watts(limit)}`}
        disabled={busy !== undefined || power?.state === 'applied' || power?.state === 'failed'} onPress={() => void run('low-power')} />}
    </View>
    {shutdown && <Text style={styles.caption} accessibilityLiveRegion="polite">{shutdown}</Text>}
    {power && <Text style={styles.caption} accessibilityLiveRegion="polite">
      {power.state === 'applied' && power.milliwatts !== undefined ? `GPU limited to ${watts(power.milliwatts)}`
        : power.state === 'requested' ? 'Power limit requested' : power.message ?? 'Power limit unavailable.'}
    </Text>}
  </View>;
}

