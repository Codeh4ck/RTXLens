import { CameraView, useCameraPermissions, type BarcodeScanningResult } from 'expo-camera';
import * as SecureStore from 'expo-secure-store';
import { StatusBar } from 'expo-status-bar';
import { useEffect, useRef, useState, type ReactNode } from 'react';
import { AppState, Platform, Pressable, ScrollView, StyleSheet, Text, View } from 'react-native';
import { SafeAreaProvider, SafeAreaView } from 'react-native-safe-area-context';
import Svg, { Path } from 'react-native-svg';

import { decodeFrame, decodePairingCode, decodeSavedComputer, requestPairing, type PairedComputer, type SensorFrame } from './protocol';
import { transport, type StreamUpdate } from './transport';

const computerKey = 'rtxlens.paired-computer';
const colors = {
  background: '#0b111b', surface: '#121d2c', border: '#293b51', text: '#eaf2fc',
  secondary: '#abbcd0', accent: '#50e3c2', warning: '#ffc36e', warningSurface: '#342719',
};

const paths = {
  scan: 'M4 8V4h4m8 0h4v4m0 8v4h-4m-8 0H4v-4m1-4h14',
  close: 'm6 6 12 12M18 6 6 18',
  home: 'm3 10 9-7 9 7v10H3zm6 10v-6h6v6',
  away: 'M3 20h18M7 17l5-14 5 14M9 12h6',
  refresh: 'M20 7v5h-5m5-1a8 8 0 1 0-2 7',
};

type Route = 'local' | 'remote';
type Connection = 'idle' | 'connecting' | StreamUpdate['state'];

export default function App() {
  return (
    <SafeAreaProvider>
      <StatusBar style="light" />
      <SafeAreaView style={styles.screen}>
        <MobilePreview />
      </SafeAreaView>
    </SafeAreaProvider>
  );
}

function MobilePreview() {
  const [permission, requestPermission] = useCameraPermissions();
  const [computer, setComputer] = useState<PairedComputer | null>(null);
  const [loaded, setLoaded] = useState(false);
  const [scanning, setScanning] = useState(false);
  const [pairing, setPairing] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [route, setRoute] = useState<Route>('local');
  const [active, setActive] = useState(AppState.currentState !== 'background');
  const [connection, setConnection] = useState<Connection>('idle');
  const [frame, setFrame] = useState<SensorFrame | null>(null);
  const [retry, setRetry] = useState(0);
  const scanReady = useRef(false);
  const pairingJob = useRef<AbortController | null>(null);
  const nextStream = useRef(0);

  useEffect(() => {
    let live = true;
    if (transport) {
      void SecureStore.getItemAsync(computerKey).then(value => {
        if (live && value) {
          setComputer(decodeSavedComputer(value));
        }
      }).catch(() => {
        if (live) {
          setError('The saved pairing could not be read.');
        }
      }).finally(() => {
        if (live) {
          setLoaded(true);
        }
      });
    } else {
      setLoaded(true);
    }

    const subscription = AppState.addEventListener('change', state => setActive(state === 'active'));
    return () => {
      live = false;
      pairingJob.current?.abort();
      subscription.remove();
    };
  }, []);

  useEffect(() => {
    if (!transport || !computer || !active) {
      return;
    }

    const native = transport;
    const id = String(++nextStream.current);
    const address = route === 'local' ? computer.localAddress : computer.remoteAddress;
    setConnection('connecting');
    setFrame(null);
    setError(null);

    const states = native.addListener('onStreamState', event => {
      if (event.id === id) {
        setConnection(event.state);
        setError(event.message ?? null);
      }
    });
    const frames = native.addListener('onFrame', event => {
      if (event.id === id) {
        const decoded = decodeFrame(event.json);
        if (decoded) {
          setFrame(previous => previous && previous.sequence >= decoded.sequence ? previous : decoded);
        }
      }
    });

    native.openStream(id, address, computer.publicKeyFingerprint, computer.token);
    return () => {
      states.remove();
      frames.remove();
      native.closeStream(id);
    };
  }, [active, computer, retry, route]);

  async function startScan() {
    setError(null);
    try {
      const access = permission?.granted ? permission : await requestPermission();
      if (!access.granted) {
        setError('Allow camera access in Settings to scan the pairing code.');
        return;
      }

      scanReady.current = true;
      setScanning(true);
    } catch {
      setError('The camera could not be opened.');
    }
  }

  async function onScan({ data }: BarcodeScanningResult) {
    if (!scanReady.current || !transport) {
      return;
    }

    scanReady.current = false;
    setScanning(false);
    setError(null);
    const native = transport;
    const job = new AbortController();
    pairingJob.current = job;

    try {
      const code = decodePairingCode(data);
      setPairing('Contacting your PC…');
      const device = Platform.OS === 'ios' ? 'iPhone' : 'Android phone';
      const paired = await requestPairing(code, device,
        (path, json) => native.post(code.localAddress, code.publicKeyFingerprint, path, json),
        () => setPairing('Approve the pairing request in RTX Lens on your PC.'), job.signal);

      if (paired && !job.signal.aborted) {
        setPairing('Saving the pairing…');
        await SecureStore.setItemAsync(computerKey, JSON.stringify(paired), {
          keychainAccessible: SecureStore.WHEN_UNLOCKED_THIS_DEVICE_ONLY,
        });
        setRoute('local');
        setComputer(paired);
      }
    } catch (failure) {
      if (!job.signal.aborted) {
        setError(failure instanceof Error ? failure.message : 'Pairing failed.');
      }
    } finally {
      if (pairingJob.current === job) {
        pairingJob.current = null;
        setPairing(null);
      }
    }
  }

  function cancelPairing() {
    scanReady.current = false;
    setScanning(false);
    pairingJob.current?.abort();
    pairingJob.current = null;
    setPairing(null);
  }

  async function forgetComputer() {
    try {
      await SecureStore.deleteItemAsync(computerKey);
      setComputer(null);
      setConnection('idle');
      setFrame(null);
      setError(null);
    } catch {
      setError('The pairing could not be removed from this phone.');
    }
  }

  return (
    <ScrollView contentContainerStyle={styles.page}>
      <View style={styles.brand}>
        <View style={styles.brandBar} />
        <View>
          <Text style={styles.title}>RTX Lens</Text>
          <Text style={styles.caption}>Live monitoring preview</Text>
        </View>
      </View>

      {error && <Text accessibilityRole="alert" style={styles.warning}>{error}</Text>}

      {!loaded ? <Text style={styles.caption}>Loading…</Text> : !transport ? (
        <Card title="Native build required">
          <Text style={styles.caption}>Install the RTX Lens IPA or APK to pair with your PC.</Text>
        </Card>
      ) : computer ? (
        <>
          <Card title="Connection">
            <Text style={styles.state}>{connectionLabel(connection)}</Text>
            <Text style={styles.caption}>{route === 'local' ? computer.localAddress : computer.remoteAddress}</Text>
            <View style={styles.actions}>
              <Button icon="home" label="Home Wi-Fi" active={route === 'local'} onPress={() => setRoute('local')} />
              <Button icon="away" label="Away" active={route === 'remote'} onPress={() => setRoute('remote')} />
            </View>
            <View style={styles.actions}>
              {(connection === 'closed' || connection === 'error') && (
                <Button icon="refresh" label="Reconnect" onPress={() => setRetry(value => value + 1)} />
              )}
              <Button icon="close" label="Forget PC" onPress={forgetComputer} />
            </View>
          </Card>
          {frame && <Readings frame={frame} live={connection === 'connected'} />}
        </>
      ) : (
        <Card title="Pair a PC">
          <Text style={styles.caption}>
            On your PC, open Configuration → Mobile Access and create a pairing code. Keep both devices on the same Wi-Fi.
          </Text>
          {scanning && (
            <View style={styles.camera}>
              <CameraView
                style={StyleSheet.absoluteFill}
                barcodeScannerSettings={{ barcodeTypes: ['qr'] }}
                onBarcodeScanned={onScan}
              />
            </View>
          )}
          {pairing && <Text style={styles.state}>{pairing}</Text>}
          <View style={styles.actions}>
            {(scanning || (pairing && pairing !== 'Saving the pairing…')) && (
              <Button icon="close" label="Cancel" onPress={cancelPairing} />
            )}
            {!scanning && !pairing && <Button icon="scan" label="Scan pairing code" active onPress={startScan} />}
          </View>
        </Card>
      )}
    </ScrollView>
  );
}

function connectionLabel(connection: Connection) {
  switch (connection) {
    case 'connected': return 'Connected';
    case 'connecting': return 'Connecting…';
    case 'error':
    case 'closed': return 'Disconnected';
    default: return 'Ready';
  }
}

function Readings({ frame, live }: { frame: SensorFrame; live: boolean }) {
  const activeWarnings = frame.warnings.filter(warning => warning.phase === 'Active');
  const available = frame.sensors.filter(sensor => sensor.value !== null && sensor.quality === 'Valid');
  return (
    <Card title={live ? 'Live readings' : 'Last readings'}>
      <Text style={styles.caption}>
        {new Date(frame.timestamp).toLocaleTimeString()} · Frame {frame.sequence} · {available.length} readings
      </Text>
      {activeWarnings.map(warning => <Text key={warning.episodeId} style={styles.warning}>{warning.title}</Text>)}
      {available.slice(0, 12).map(sensor => (
        <View key={sensor.identity.key} style={styles.reading}>
          <View style={styles.readingLabel}>
            <Text style={styles.text}>{sensor.identity.metric.replace(/([a-z])([A-Z])/g, '$1 $2')}</Text>
            <Text style={styles.caption}>{sensor.identity.pluginId} · {sensor.identity.component}</Text>
          </View>
          <Text style={styles.value}>{sensor.value?.toFixed(2)} {sensor.unit}</Text>
        </View>
      ))}
    </Card>
  );
}

function Card({ title, children }: { title: string; children: ReactNode }) {
  return (
    <View style={styles.card}>
      <Text style={styles.heading}>{title}</Text>
      {children}
    </View>
  );
}

function Button({ icon, label, active = false, onPress }: {
  icon: keyof typeof paths; label: string; active?: boolean; onPress: () => void;
}) {
  const color = active ? colors.accent : colors.text;
  return (
    <Pressable accessibilityRole="button" onPress={onPress} style={[styles.button, active && styles.activeButton]}>
      <Svg width={16} height={16} viewBox="0 0 24 24" fill="none">
        <Path d={paths[icon]} stroke={color} strokeWidth={1.8} strokeLinecap="round" strokeLinejoin="round" />
      </Svg>
      <Text style={[styles.buttonLabel, { color }]}>{label}</Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  screen: { flex: 1, backgroundColor: colors.background },
  page: { padding: 18, gap: 14 },
  brand: { flexDirection: 'row', gap: 12, alignItems: 'stretch', paddingVertical: 6 },
  brandBar: { backgroundColor: colors.accent, width: 4, borderRadius: 3 },
  title: { color: colors.text, fontSize: 29, fontWeight: '700' },
  caption: { color: colors.secondary, fontSize: 14, lineHeight: 20 },
  card: { backgroundColor: colors.surface, borderColor: colors.border, borderWidth: 1, borderRadius: 12, padding: 15, gap: 10 },
  heading: { color: colors.text, fontSize: 18, fontWeight: '600' },
  state: { color: colors.accent, fontSize: 15, fontWeight: '600' },
  warning: { color: colors.warning, backgroundColor: colors.warningSurface, borderRadius: 8, padding: 10, lineHeight: 20 },
  camera: { height: 240, borderRadius: 8, overflow: 'hidden' },
  actions: { flexDirection: 'row', flexWrap: 'wrap', gap: 8 },
  button: { flexDirection: 'row', gap: 7, alignItems: 'center', minHeight: 44, paddingHorizontal: 12,
    borderRadius: 8, borderWidth: 1, borderColor: colors.border, backgroundColor: colors.background },
  activeButton: { borderColor: colors.accent },
  buttonLabel: { fontSize: 14, fontWeight: '600' },
  text: { color: colors.text, fontSize: 15 },
  reading: { flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center', gap: 12,
    borderTopColor: colors.border, borderTopWidth: 1, paddingTop: 8 },
  readingLabel: { flex: 1 },
  value: { color: colors.text, fontWeight: '600', maxWidth: '45%', textAlign: 'right' },
});
