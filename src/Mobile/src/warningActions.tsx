import { useEffect, useRef, useState } from 'react';
import { StyleSheet, Switch, Text, View, type GestureResponderEvent } from 'react-native';

import { useColors, useThemeStyles, type Colors } from './appearance';
import { mobileProtocol } from './mobileProtocol.ts';
import { AsusId, NvidiaId } from './pluginIds.ts';
import type { PairedComputer, WarningState } from './protocol';
import { transport } from './transport';
import { Button, Card, Notice, Selection, useStyles } from './ui';

type Settings = { enableShutdown: boolean; enableLowPower: boolean; gpuId?: string | null; powerMilliwatts?: number | null };
type Target = { id: string; name: string; minimum: number; maximum: number };
type Options = { allowed: boolean; settings: Settings; targets: Target[];
  profiles: { name: string; gpuId: string; powerMilliwatts: number }[] };
type Action = 'shutdown' | 'low-power';
type CommandResult = { state: string; milliwatts?: number; message?: string };

const watts = (milliwatts: number) => `${milliwatts / 1000} W`;

export function useWarningActions(computer: PairedComputer | null, address: string | null | undefined, settingsOpen: boolean) {
  const [snapshot, setSnapshot] = useState<{ key: string; options?: Options; message?: string }>();
  const [revision, setRevision] = useState(0);
  const key = computer && address ? `${computer.token}|${address}` : null;
  const client = transport;
  const supported = typeof client?.postWarningAction === 'function';

  useEffect(() => {
    if (!supported || !client || !computer || !address || !key) return;
    let current = true;
    void client.get(address, computer.publicKeyFingerprint, mobileProtocol.WarningActions, computer.token)
      .then(reply => {
        if (!current) return;
        if (reply.status === 404) {
          setSnapshot({ key, message: 'Update RTX Lens on the PC to use warning actions.' });
        } else if (reply.status === 200) {
          const options: Options = JSON.parse(reply.body);
          if (typeof options.allowed !== 'boolean' || !options.settings || !Array.isArray(options.targets) || !Array.isArray(options.profiles))
            throw new Error('Invalid warning action options.');
          setSnapshot({ key, options });
        } else {
          throw new Error('Warning action settings could not be read.');
        }
      }).catch(() => {
        if (current) setSnapshot({ key, message: 'Could not load warning action settings. Reopen Settings to retry.' });
      });
    return () => { current = false; };
  }, [client, computer, address, key, supported, settingsOpen, revision]);

  async function post(path: string, body: unknown) {
    if (!client || !supported || !computer || !address)
      throw new Error('Connect to the PC first.');
    return client.postWarningAction(address, computer.publicKeyFingerprint, computer.token, path, JSON.stringify(body));
  }

  async function save(settings: Settings) {
    const reply = await post(mobileProtocol.WarningActions, settings);
    if (reply.status === 403) {
      setRevision(value => value + 1);
      throw new Error('Allow warning actions for this phone in RTX Lens on the PC.');
    }
    if (reply.status !== 204)
      throw new Error('Check that the selected GPU is available and the wattage is in range.');
    setSnapshot(previous => previous?.key === key && previous?.options
      ? { ...previous, options: { ...previous.options, settings } } : previous);
    setRevision(value => value + 1);
  }

  async function run(instanceId: string, episodeId: string, action: Action, statusOnly = false): Promise<CommandResult> {
    const reply = await post(mobileProtocol.RunWarningAction, { instanceId, episodeId, action, statusOnly });
    if (reply.status === 403) {
      setRevision(value => value + 1);
      throw new Error('This phone is no longer allowed to run that action.');
    }
    if (reply.status !== 200)
      throw new Error('The PC did not accept the warning action.');
    return JSON.parse(reply.body) as CommandResult;
  }

  return { options: key && snapshot?.key === key ? snapshot?.options : undefined,
    message: !supported ? 'Install a mobile build that supports warning actions.' : key && snapshot?.key === key ? snapshot?.message : undefined,
    connected: key !== null, save, run };
}

export type WarningActions = ReturnType<typeof useWarningActions>;

export function WarningActionSettings({ actions }: { actions: WarningActions }) {
  const styles = useStyles();
  if (!actions.options?.allowed) {
    const message = actions.message ?? (!actions.connected ? 'Connect to your PC to configure warning actions.'
      : actions.options ? 'On the PC, open Configuration → Mobile Access and allow warning actions for this phone.' : 'Loading warning action settings…');
    return <Card title="Warning actions" icon="warnings"><Text style={styles.caption}>{message}</Text></Card>;
  }

  return <Editor key={JSON.stringify(actions.options.settings)} options={actions.options} save={actions.save} />;
}

function Editor({ options, save }: { options: Options; save: WarningActions['save'] }) {
  const styles = useStyles();
  const colors = useColors();
  const [draft, setDraft] = useState(options.settings);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const selected = options.targets.find(target => target.id === draft.gpuId)
    ?? (options.targets.length === 1 ? options.targets[0] : undefined);
  const milliwatts = selected ? Math.max(selected.minimum, Math.min(selected.maximum, draft.powerMilliwatts ?? selected.minimum)) : undefined;
  const conflicts = draft.enableLowPower && selected && milliwatts !== undefined
    ? options.profiles.filter(profile => profile.gpuId === selected.id && profile.powerMilliwatts !== milliwatts) : [];
  const dirty = draft.enableShutdown !== options.settings.enableShutdown || draft.enableLowPower !== options.settings.enableLowPower
    || draft.gpuId !== options.settings.gpuId || draft.powerMilliwatts !== options.settings.powerMilliwatts
    || (draft.enableLowPower && !!selected && milliwatts !== options.settings.powerMilliwatts);

  async function apply() {
    setBusy(true);
    setError(undefined);
    try {
      await save(draft.enableLowPower ? { ...draft, gpuId: selected?.id, powerMilliwatts: milliwatts } : draft);
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : 'Could not save warning actions.');
    } finally {
      setBusy(false);
    }
  }

  return <Card title="Warning actions" icon="warnings">
    <View style={styles.row}>
      <Text style={[styles.text, { flex: 1 }]}>Enable shutdown command on warning</Text>
      <Switch accessibilityLabel="Enable shutdown command on warning" value={draft.enableShutdown}
        trackColor={{ false: colors.raised, true: colors.accentSurface }} thumbColor={draft.enableShutdown ? colors.accent : colors.muted}
        disabled={busy} onValueChange={enableShutdown => setDraft({ ...draft, enableShutdown })} />
    </View>
    <View style={styles.row}>
      <Text style={[styles.text, { flex: 1 }]}>Enable low power command on warning</Text>
      <Switch accessibilityLabel="Enable low power command on warning" value={draft.enableLowPower}
        trackColor={{ false: colors.raised, true: colors.accentSurface }} thumbColor={draft.enableLowPower ? colors.accent : colors.muted}
        disabled={busy || (!draft.enableLowPower && options.targets.length === 0)}
        onValueChange={enableLowPower => setDraft({ ...draft, enableLowPower })} />
    </View>
    {options.targets.length === 0 && <Text style={styles.caption}>The PC has no available GPU power-limit controls.</Text>}
    {draft.enableLowPower && options.targets.length > 1 &&
      <Selection options={options.targets.map(target => ({ id: target.id, label: target.name }))}
        selected={selected?.id ?? ''} onSelect={gpuId => {
          if (!busy) setDraft({ ...draft, gpuId, powerMilliwatts: options.targets.find(target => target.id === gpuId)?.minimum });
        }} />}
    {draft.enableLowPower && selected && milliwatts !== undefined && <>
      <Text style={styles.caption}>{selected.name}</Text>
      <PowerSlider target={selected} value={milliwatts} disabled={busy}
        change={powerMilliwatts => setDraft({ ...draft, gpuId: selected.id, powerMilliwatts })} />
      {conflicts.map((profile, index) => <Text key={index} style={[styles.caption, { color: colors.warning }]}>
        {profile.name} is set to {watts(profile.powerMilliwatts)}. If both apply, RTX Lens will use {watts(Math.min(profile.powerMilliwatts, milliwatts))}.
      </Text>)}
    </>}
    {error && <Notice>{error}</Notice>}
    <Button icon="check" label="Apply" disabled={!dirty || busy || (draft.enableLowPower && !selected)} onPress={() => void apply()} />
  </Card>;
}

function PowerSlider({ target, value, disabled, change }: { target: Target; value: number; disabled: boolean; change: (value: number) => void }) {
  const styles = useStyles();
  const local = useThemeStyles(sliderStyles);
  const geometry = useRef({ width: 0, origin: 0 });
  const range = target.maximum - target.minimum;
  const position = range > 0 ? (value - target.minimum) / range : 0;
  const clamp = (amount: number) => Math.min(target.maximum, Math.max(target.minimum, amount));

  function move(event: GestureResponderEvent, begin = false) {
    const { pageX, locationX } = event.nativeEvent;
    if (begin) geometry.current.origin = pageX - locationX;
    const fraction = (pageX - geometry.current.origin) / geometry.current.width;
    if (!Number.isFinite(fraction)) return;
    if (fraction <= 0) change(target.minimum);
    else if (fraction >= 1) change(target.maximum);
    else change(clamp(Math.round((target.minimum + fraction * range) / 1000) * 1000));
  }

  return <View>
    <Text style={styles.text}>Mobile GPU power limit · {watts(value)}</Text>
    <View accessible accessibilityRole="adjustable" accessibilityLabel="Mobile GPU power limit"
      accessibilityValue={{ min: target.minimum / 1000, max: target.maximum / 1000, now: value / 1000, text: watts(value) }}
      accessibilityState={{ disabled }} accessibilityActions={[{ name: 'increment' }, { name: 'decrement' }]}
      onAccessibilityAction={event => {
        if (!disabled) change(clamp(value + (event.nativeEvent.actionName === 'increment' ? 1000 : -1000)));
      }}
      onStartShouldSetResponder={() => !disabled && range > 0}
      onResponderGrant={event => move(event, true)} onResponderMove={moveEvent => move(moveEvent)}
      onLayout={event => { geometry.current.width = event.nativeEvent.layout.width; }}
      style={local.touch}>
      <View pointerEvents="none" style={local.track}>
        <View style={[local.fill, { width: `${position * 100}%` }]} />
        <View style={[local.thumb, { left: `${position * 100}%` }]} />
      </View>
    </View>
    <View style={[styles.row, { justifyContent: 'space-between' }]}>
      <Text style={styles.caption}>{watts(target.minimum)}</Text><Text style={styles.caption}>{watts(target.maximum)}</Text>
    </View>
  </View>;
}

const sliderStyles = (colors: Colors) => StyleSheet.create({
  touch: { minHeight: 44, justifyContent: 'center' },
  track: { height: 6, borderRadius: 3, backgroundColor: colors.line },
  fill: { height: 6, borderRadius: 3, backgroundColor: colors.accent },
  thumb: { position: 'absolute', marginLeft: -7, top: -4, width: 14, height: 14, borderRadius: 7, backgroundColor: colors.accent },
});

export function ActiveWarningActions({ warning, instanceId, actions }: { warning: WarningState; instanceId: string; actions: WarningActions }) {
  const styles = useStyles();
  const [busy, setBusy] = useState<Action>();
  const [shutdown, setShutdown] = useState<string>();
  const [power, setPower] = useState<CommandResult>();
  const commandPending = useRef(false);
  const settings = actions.options?.allowed ? actions.options.settings : undefined;
  const target = actions.options?.targets.find(item => item.id === settings?.gpuId);
  const matchesGpu = warning.pluginId !== AsusId && warning.pluginId !== NvidiaId || warning.deviceId === settings?.gpuId;
  const showPower = settings?.enableLowPower && matchesGpu && target && settings.powerMilliwatts != null
    && settings.powerMilliwatts >= target.minimum && settings.powerMilliwatts <= target.maximum;
  if (!settings?.enableShutdown && !showPower) return null;

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
      {showPower && <Button icon="power" label={power?.state === 'requested' ? 'Check power limit' : `Limit GPU to ${watts(settings!.powerMilliwatts!)}`}
        disabled={busy !== undefined || power?.state === 'applied' || power?.state === 'failed'} onPress={() => void run('low-power')} />}
    </View>
    {shutdown && <Text style={styles.caption} accessibilityLiveRegion="polite">{shutdown}</Text>}
    {power && <Text style={styles.caption} accessibilityLiveRegion="polite">
      {power.state === 'applied' && power.milliwatts !== undefined ? `GPU limited to ${watts(power.milliwatts)}`
        : power.state === 'requested' ? 'Power limit requested' : power.message ?? 'Power limit unavailable.'}
    </Text>}
  </View>;
}

