import * as SecureStore from 'expo-secure-store';
import { createContext, useEffect, useRef, useState } from 'react';
import { Pressable, StyleSheet, Switch, Text, View } from 'react-native';

import { sessionResult } from './connection';
import { groupMetrics, pinPanelSources, pinPanelTitle } from './metrics';
import {
  allowsWarning, decodeDisplayPreferences, decodeNotificationPreferences, defaultDisplayPreferences, defaultNotificationPreferences,
  homeCards, homePanelKey, moveHomePanel, warningCategories, type DisplayPreferences, type HomePanel, type NotificationPreferences, type TemperatureUnit,
} from './mobilePreferences';
import type { PairedComputer, SensorReading } from './protocol';
import { transport } from './transport';
import { Button, Card, Icon, Notice, Selection, colors, styles } from './ui';

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
    notificationRef.current = value;
    setNotifications(value);
    try {
      await SecureStore.setItemAsync(notificationKey, JSON.stringify(value));
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
      await SecureStore.setItemAsync(displayKey, JSON.stringify(value));
      setDisplay(value);
    } finally {
      displayWritePending.current = false;
      setSavingDisplay(false);
    }
  }

  return { loaded, error, notifications, notificationRef, temperature, display, savingDisplay, saveNotifications, saveTemperature, saveDisplay };
}

export function TestRemoteAccess({ computer, wifi }: { computer: PairedComputer; wifi: boolean }) {
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
      {!hasRemoteAddress && <Text style={styles.caption}>Set a remote address in Mobile Access on your PC and pair again.</Text>}
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

export function NotificationPreferencesCard({ value, syncRequired, canSync, retrySave, save }: {
  value: NotificationPreferences; syncRequired: boolean; canSync: boolean; retrySave: boolean;
  save: (value: NotificationPreferences) => Promise<void>;
}) {
  const [draft, setDraft] = useState(value);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const changed = draft.categories !== value.categories || draft.showTitle !== value.showTitle;

  async function apply() {
    setSaving(true);
    setError(null);
    try {
      await save(draft);
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : 'Could not save notification preferences.');
    } finally {
      setSaving(false);
    }
  }

  return (
    <Card title="Notification preferences" icon="warnings">
      <Text style={styles.heading}>Warning filters</Text>
      {filters.map(({ category, label }) => (
        <View key={category} style={styles.row}>
          <Text style={[styles.text, { flex: 1 }]}>{label}</Text>
          <Switch accessibilityLabel={label} value={allowsWarning(draft, category)} disabled={saving}
            trackColor={{ true: colors.accentSurface, false: colors.line }}
            onValueChange={on => setDraft(previous => ({
              ...previous, categories: on ? previous.categories | category : previous.categories & ~category,
            }))} />
        </View>
      ))}
      <Text style={styles.heading}>Lock-screen privacy</Text>
      <Selection options={[{ id: 'title', label: 'Show warning title' }, { id: 'generic', label: 'Hide warning title' }]}
        selected={draft.showTitle ? 'title' : 'generic'}
        onSelect={mode => { if (!saving) setDraft(previous => ({ ...previous, showTitle: mode === 'title' })); }} />
      {syncRequired && !canSync && <Text style={styles.caption}>Connect to your PC to apply these settings to lock-screen delivery.</Text>}
      {error && <Notice>{error}</Notice>}
      <Button icon="check" label={saving ? 'Saving…' : 'Save notification preferences'} variant="primary"
        disabled={(!changed && !retrySave) || saving || (syncRequired && !canSync)} onPress={() => void apply()} />
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

export function DashboardSettings({ value, busy, sensors, save }: {
  value: DisplayPreferences; busy: boolean; sensors: SensorReading[]; save: (value: DisplayPreferences) => Promise<void>;
}) {
  const [error, setError] = useState<string | null>(null);
  const active = new Set(value.homePanels.map(homePanelKey));
  const pinSources = pinPanelSources(groupMetrics(sensors, 'power'));
  const reportingPins = [...pinSources.values()].filter(source => source.pins.some(reading => reading.quality === 'Valid' && reading.value !== null));
  const options: { panel: HomePanel; title: string }[] = [
    ...homeCards.map(card => ({ panel: { kind: 'metric' as const, id: card.id }, title: card.label })),
    ...reportingPins,
  ];
  const available = options.filter(option => !active.has(homePanelKey(option.panel)));

  function titleFor(panel: HomePanel) {
    if (panel.kind === 'metric') return homeCards.find(card => card.id === panel.id)?.label ?? panel.id;
    return pinSources.get(homePanelKey(panel))?.title ?? pinPanelTitle(panel);
  }

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
      <Card title="Home screen" icon="home">
        {value.homePanels.map((panel, index) => {
          const title = titleFor(panel);
          return (
            <View key={homePanelKey(panel)} style={styles.row}>
              <Text style={[styles.text, { flex: 1 }]}>{title}</Text>
              <View style={dashboardStyles.actions}>
                <PanelAction icon="up" label={`Move ${title} up`} disabled={busy || index === 0}
                  onPress={() => void change({ ...value, homePanels: moveHomePanel(value.homePanels, index, -1) })} />
                <PanelAction icon="down" label={`Move ${title} down`} disabled={busy || index === value.homePanels.length - 1}
                  onPress={() => void change({ ...value, homePanels: moveHomePanel(value.homePanels, index, 1) })} />
                <PanelAction icon="close" label={`Remove ${title}`} disabled={busy}
                  onPress={() => void change({ ...value, homePanels: value.homePanels.filter((_, current) => current !== index) })} />
              </View>
            </View>
          );
        })}
        {available.length > 0 && <Text style={styles.heading}>Add panels</Text>}
        {available.map(({ panel, title }) => (
          <Button key={homePanelKey(panel)} icon="add" label={`Add ${title}`} disabled={busy}
            onPress={() => void change({ ...value, homePanels: [...value.homePanels, panel] })} />
        ))}
        {reportingPins.length === 0 && <Text style={styles.caption}>Pin panels can be added when a device is reporting pin readings.</Text>}
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

function PanelAction({ icon, label, disabled, onPress }: {
  icon: 'up' | 'down' | 'close'; label: string; disabled: boolean; onPress: () => void;
}) {
  return (
    <Pressable accessibilityRole="button" accessibilityLabel={label} accessibilityState={{ disabled }} disabled={disabled} onPress={onPress}
      style={({ pressed }) => [dashboardStyles.action, (pressed || disabled) && styles.pressed]}>
      <Icon name={icon} color={disabled ? colors.muted : colors.text} size={18} />
    </Pressable>
  );
}

const dashboardStyles = StyleSheet.create({
  actions: { flexDirection: 'row', gap: 4 },
  action: { width: 44, height: 44, alignItems: 'center', justifyContent: 'center', borderRadius: 10, backgroundColor: colors.raised },
});
