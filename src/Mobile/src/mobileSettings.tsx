import { useColors } from './appearance';
import * as SecureStore from 'expo-secure-store';
import { createContext, useEffect, useRef, useState } from 'react';
import { Switch, Text, TextInput, View } from 'react-native';

import { sessionResult } from './connection';
import {
  allowsWarning, decodeDisplayPreferences, decodeNotificationPreferences, defaultDisplayPreferences, defaultNotificationPreferences,
  warningCategories, type DisplayPreferences, type NotificationPreferences, type TemperatureUnit,
} from './mobilePreferences';
import { readAddress, type PairedComputer } from './protocol';
import { transport } from './transport';
import { Button, Card, Notice, Selection, useStyles } from './ui';

const notificationKey = 'rtxlens.notification-preferences';
const temperatureKey = 'rtxlens.temperature-unit';
const displayKey = 'rtxlens.display-preferences';

export const TemperatureUnitContext = createContext<TemperatureUnit>('celsius');

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
      const reply = await transport.get(remoteAddress, publicKeyFingerprint, '/session', token);
      const status = sessionResult(reply);
      const message = {
        connect: 'The PC is reachable through its remote address.',
        revoked: 'The PC no longer authorizes this phone. Pair it again in Mobile Access.',
        'changed-key': 'The PC’s key did not match at its remote address. Check the saved address and pairing.',
        retry: 'Could not reach the PC through its remote address.',
      }[status];
      setResult({ success: status === 'connect', message });
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

export function NotificationPreferencesCard({ value, syncRequired, canSync, save }: {
  value: NotificationPreferences; syncRequired: boolean; canSync: boolean;
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

  return (
    <Card title="Notification preferences" icon="warnings">
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
      {syncRequired && !canSync && <Text style={styles.caption}>Connect to your PC to apply these settings to lock-screen delivery.</Text>}
      {error && <Notice>{error}</Notice>}
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

export function MsiDisplaySettings({ value, busy, save }: {
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
