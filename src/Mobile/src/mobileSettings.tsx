import { formatReading, number } from './metrics';
import type { SensorReading } from './protocol';
import { mobileProtocol } from './mobileProtocol.ts';
import { useColors } from './appearance';
import * as SecureStore from 'expo-secure-store';
import { createContext, useEffect, useRef, useState } from 'react';
import { Switch, Text, TextInput, View } from 'react-native';

import { sessionResult } from './connection';
import type { useLockScreenNotifications } from './lockScreenNotifications';
import {
  allowsWarning, decodeDisplayPreferences, decodeNotificationPreferences, defaultDisplayPreferences, defaultNotificationPreferences,
  warningCategories, type DisplayPreferences, type NotificationPreferences, type TemperatureUnit,
} from './mobilePreferences';
import { readAddress, type PairedComputer } from './protocol';
import { transport } from './transport';
import { Button, Card, Notice, PanelList, Selection, useStyles } from './ui';

const notificationKey = 'rtxlens.notification-preferences';
const temperatureKey = 'rtxlens.temperature-unit';
const displayKey = 'rtxlens.display-preferences';

export function readingFormat(unit: TemperatureUnit, useGrouping: boolean) {
  return {
    formatReading: (reading?: SensorReading) => formatReading(reading, unit, useGrouping),
    number: (value: number, digits: number) => number(value, digits, useGrouping),
  };
}

export const ReadingFormatContext = createContext(readingFormat('celsius', false));

export function useMobilePreferences() {
  const [loaded, setLoaded] = useState(false);
  const [notifications, setNotifications] = useState(defaultNotificationPreferences);
  const [temperature, setTemperature] = useState<TemperatureUnit>('celsius');
  const [display, setDisplay] = useState(defaultDisplayPreferences);
  const [savingDisplay, setSavingDisplay] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const notificationRef = useRef(notifications);
  const displayWritePending = useRef(false);

  useEffect(() => {
    let current = true;
    const savedValues = [
      SecureStore.getItemAsync(notificationKey), SecureStore.getItemAsync(temperatureKey), SecureStore.getItemAsync(displayKey),
    ];
    void Promise.allSettled(savedValues).then(([alerts, units, layout]) => {
      if (!current) return;
      try {
        if (alerts.status === 'rejected') throw alerts.reason;
        if (alerts.value) {
          const options = decodeNotificationPreferences(alerts.value);
          notificationRef.current = options;
          setNotifications(options);
        }
      } catch {
        const fallback = { ...defaultNotificationPreferences, showTitle: false };
        notificationRef.current = fallback;
        setNotifications(fallback);
        setError('Saved notification preferences could not be read.');
      }
      if (units.status === 'fulfilled' && units.value === 'fahrenheit') setTemperature('fahrenheit');
      if (layout.status === 'fulfilled' && layout.value) {
        try {
          setDisplay(decodeDisplayPreferences(layout.value));
        } catch {
          setDisplay(defaultDisplayPreferences);
        }
      }
      setLoaded(true);
    });
    return () => { current = false; };
  }, []);

  async function saveNotifications(value: NotificationPreferences) {
    try {
      await SecureStore.setItemAsync(notificationKey, JSON.stringify(value));
      notificationRef.current = value;
      setNotifications(value);
      setError(null);
    } catch {
      setError('Notification preferences could not be saved on this phone.');
      throw new Error('Could not save notification preferences on this phone.');
    }
  }

  async function saveTemperature(value: TemperatureUnit) {
    await SecureStore.setItemAsync(temperatureKey, value);
    setTemperature(value);
  }

  async function saveDisplay(value: DisplayPreferences) {
    if (displayWritePending.current) return;
    displayWritePending.current = true;
    setSavingDisplay(true);
    try {
      await SecureStore.setItemAsync(displayKey, JSON.stringify({ ...value, layoutVersion: 2 }));
      setDisplay(value);
    } finally {
      displayWritePending.current = false;
      setSavingDisplay(false);
    }
  }

  return { loaded, error, notifications, notificationRef, temperature, display, savingDisplay, saveNotifications, saveTemperature, saveDisplay };
}

export function ConnectionAddresses({ computer, save }: {
  computer: PairedComputer; save: (computer: PairedComputer) => Promise<void>;
}) {
  const styles = useStyles();
  const [local, setLocal] = useState(computer.localAddress);
  const [remote, setRemote] = useState(computer.remoteAddress);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  async function apply() {
    const localAddress = readAddress(local.trim(), true);
    const remoteAddress = readAddress(remote.trim(), false);
    if (!localAddress || !remoteAddress) {
      setError('Enter HTTPS addresses with a port from 1024 to 65535. Use an IPv4 address for the local connection.');
      return;
    }
    setBusy(true);
    try {
      await save({ ...computer, localAddress, remoteAddress });
      setLocal(localAddress);
      setRemote(remoteAddress);
      setError('');
    } catch {
      setError('Could not save the connection addresses.');
    } finally {
      setBusy(false);
    }
  }

  return <>
    {[{ label: 'Local address', value: local, change: setLocal }, { label: 'Remote address', value: remote, change: setRemote }].map(field =>
      <View key={field.label} style={{ gap: 6 }}>
        <Text style={styles.caption}>{field.label}</Text>
        <TextInput accessibilityLabel={field.label} style={[styles.button, styles.text]} autoCapitalize="none" autoCorrect={false}
          keyboardType="url" editable={!busy} value={field.value} onChangeText={field.change} />
      </View>)}
    {error && <Notice>{error}</Notice>}
    {(local !== computer.localAddress || remote !== computer.remoteAddress) &&
      <Button icon="check" label="Save addresses" disabled={busy} onPress={() => void apply()} />}
  </>;
}

export function TestRemoteAccess({ computer, wifi }: { computer: PairedComputer; wifi: boolean }) {
  const styles = useStyles();
  const colors = useColors();
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<{ success: boolean; message: string } | null>(null);
  const hasRemoteAddress = computer.localAddress !== computer.remoteAddress;

  async function test() {
    if (!transport) return;
    setBusy(true);
    setResult(null);

    try {
      const { remoteAddress, publicKeyFingerprint, token } = computer;
      const reply = await transport.get(remoteAddress, publicKeyFingerprint, mobileProtocol.Session, token);
      const status = sessionResult(reply);
      const message = {
        connect: reply.status === 204 ? 'The PC accepted this pairing through its remote address.'
          : 'The remote address responded. Connect to check the readings stream.',
        revoked: 'The PC no longer authorizes this phone. Pair it again in Mobile Access.',
        'changed-key': 'The PC’s key did not match at its remote address. Check the saved address and pairing.',
        'local-network-denied': 'Allow Local Network access for RTX Lens in iPhone Settings and try again.',
        retry: 'Could not reach the PC through its remote address.',
      }[status];
      setResult({ success: reply.status === 204 && status === 'connect', message });
    } catch {
      setResult({ success: false, message: 'Could not reach the PC through its remote address.' });
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <Button icon="link" label={busy ? 'Testing remote access…' : 'Test remote access'} disabled={busy || !hasRemoteAddress}
        onPress={() => void test()} />
      {!hasRemoteAddress && <Text style={styles.caption}>Enter a separate remote address above to test access.</Text>}
      {result && <>
        {result.success ? <Text accessibilityRole="alert" style={{ color: colors.accent }}>{result.message}</Text> : <Notice>{result.message}</Notice>}
        {wifi && <Text style={styles.caption}>To check access from outside your network, try again over mobile data.</Text>}
      </>}
    </>
  );
}

const filters = [
  { category: warningCategories.connector, label: 'Connector faults' },
  { category: warningCategories.temperature, label: 'Temperature warnings' },
  { category: warningCategories.safeguard, label: 'Safeguard+ events' },
  { category: warningCategories.other, label: 'Other warnings' },
] as const;

export function NotificationSettings({ permission, permissionError, onEnable, onSystemSettings, push, paired, live, value, preferencesError, save }: {
  permission: 'granted' | 'available' | 'blocked';
  permissionError: string | null;
  onEnable: () => void;
  onSystemSettings: () => void;
  push: ReturnType<typeof useLockScreenNotifications>;
  paired: boolean;
  live: boolean;
  value?: NotificationPreferences;
  preferencesError: string | null;
  save: (value: NotificationPreferences) => Promise<void>;
}) {
  const styles = useStyles();
  const colors = useColors();
  const pending = useRef(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function apply(next: NotificationPreferences) {
    if (pending.current) return;
    pending.current = true;
    setSaving(true);
    setError(null);
    try {
      await save(next);
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : 'Could not save notification preferences.');
    } finally {
      pending.current = false;
      setSaving(false);
    }
  }

  const systemStatus = permission === 'granted' ? 'Allowed' : permission === 'blocked' ? 'Blocked' : 'Off';
  const lockScreenStatus = push.busy ? 'Updating…' : !push.enabled ? 'Off' : permission === 'blocked' ? 'Blocked by system'
    : push.ready ? 'Registered with PC' : live ? 'Waiting for registration' : 'Waiting for connection';

  return (
    <Card title="Notifications" icon="warnings">
      <PanelList>
        <View style={{ gap: 8, paddingBottom: 10 }}>
          <View style={[styles.row, { justifyContent: 'space-between' }]}>
            <Text style={styles.heading}>While the app is open</Text>
            <Text style={[styles.caption, permission === 'granted' && { color: colors.accent }]}>{systemStatus}</Text>
          </View>
          <Text style={styles.caption}>Show system alerts for PC warnings while RTX Lens is open and connected.</Text>
          {permissionError && <Notice>{permissionError}</Notice>}
          {permission === 'blocked' && <Button icon="settings" label="Open system settings" onPress={onSystemSettings} />}
          {permission === 'available' && <Button icon="warnings" label="Allow notifications" onPress={onEnable} />}
        </View>
        {push.available && <View style={{ gap: 8, paddingTop: 10, paddingBottom: 10 }}>
          <View style={[styles.row, { justifyContent: 'space-between' }]}>
            <Text style={styles.heading}>Lock-screen alerts</Text>
            <Text style={[styles.caption, push.ready && permission === 'granted' && { color: colors.accent }]}>{lockScreenStatus}</Text>
          </View>
          <Text style={styles.caption}>{!paired ? 'Pair your PC to set up lock-screen alerts.'
            : 'Receive PC warnings while RTX Lens is closed, through Expo and your phone’s push service.'}</Text>
          {push.error && <Notice>{push.error}</Notice>}
          {push.enabled && !push.ready && live && permission === 'granted' && !preferencesError &&
            <Button icon="refresh" label="Retry registration" disabled={push.busy} onPress={() => void push.set(true)} />}
          {paired && <Button icon="warnings" label={push.enabled ? 'Turn off lock-screen alerts' : 'Enable lock-screen alerts'}
            disabled={!live || push.busy || (!push.enabled && (permission === 'blocked' || !value || !!preferencesError))}
            onPress={() => void push.set(!push.enabled, !push.enabled)} />}
          {paired && !live && <Text style={styles.caption}>Connect to your PC to change lock-screen delivery.</Text>}
        </View>}
      </PanelList>
      {value && <>
        <Text style={styles.heading}>Warning filters</Text>
        {filters.map(({ category, label }) => (
          <View key={category} style={styles.row}>
            <Text style={[styles.text, { flex: 1 }]}>{label}</Text>
            <Switch accessibilityLabel={label} value={allowsWarning(value, category)} disabled={saving}
              trackColor={{ true: colors.accentSurface, false: colors.line }}
              onValueChange={on => void apply({
                ...value, categories: on ? value.categories | category : value.categories & ~category,
              })} />
          </View>
        ))}
        <Text style={styles.heading}>Lock-screen privacy</Text>
        <Selection options={[{ id: 'title', label: 'Show warning title' }, { id: 'generic', label: 'Hide warning title' }]}
          selected={value.showTitle ? 'title' : 'generic'}
          onSelect={mode => { if (!saving) void apply({ ...value, showTitle: mode === 'title' }); }} />
        {push.enabled && !live && <Text style={styles.caption}>Filter and privacy changes sync when your PC reconnects.</Text>}
      </>}
      {error && <Notice>{error}</Notice>}
      {!error && preferencesError && <Notice>{preferencesError}</Notice>}
    </Card>
  );
}

export function TemperatureSettings({ unit, save }: { unit: TemperatureUnit; save: (value: TemperatureUnit) => Promise<void> }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function change(value: TemperatureUnit) {
    if (busy || value === unit) return;
    setBusy(true);
    setError(null);
    try {
      await save(value);
    } catch {
      setError('Could not save the temperature unit.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card title="Temperature units" icon="temp">
      <Selection options={[{ id: 'celsius', label: 'Celsius (°C)' }, { id: 'fahrenheit', label: 'Fahrenheit (°F)' }]}
        selected={unit} onSelect={value => void change(value)} />
      {error && <Notice>{error}</Notice>}
    </Card>
  );
}

export function DisplaySettings({ value, busy, save }: {
  value: DisplayPreferences; busy: boolean; save: (value: DisplayPreferences) => Promise<void>;
}) {
  const styles = useStyles();
  const colors = useColors();
  const [error, setError] = useState<string | null>(null);
  async function change(next: DisplayPreferences) {
    setError(null);
    try {
      await save(next);
    } catch {
      setError('Could not save display settings.');
    }
  }

  return (
    <>
      <Card title="Number format" icon="activity">
        <View style={styles.row}>
          <Text style={[styles.text, { flex: 1 }]}>Use thousands separator</Text>
          <Switch accessibilityLabel="Use thousands separator" value={value.useGrouping} disabled={busy}
            trackColor={{ true: colors.accentSurface, false: colors.line }}
            onValueChange={useGrouping => void change({ ...value, useGrouping })} />
        </View>
        <Text style={styles.caption}>{number(17500, 0, value.useGrouping)} MHz</Text>
      </Card>
      <Card title="MSI PSU" icon="power">
        <View style={styles.row}>
          <Text style={[styles.text, { flex: 1 }]}>Hide connectors with zero current</Text>
          <Switch accessibilityLabel="Hide connectors with zero current" value={value.hideIdleMsiConnectors} disabled={busy}
            trackColor={{ true: colors.accentSurface, false: colors.line }}
            onValueChange={hideIdleMsiConnectors => void change({ ...value, hideIdleMsiConnectors })} />
        </View>
      </Card>
      {error && <Notice>{error}</Notice>}
    </>
  );
}
