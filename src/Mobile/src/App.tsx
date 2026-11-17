import { CameraView, useCameraPermissions, type BarcodeScanningResult } from 'expo-camera';
import { useNetworkState, NetworkStateType } from 'expo-network';
import * as SecureStore from 'expo-secure-store';
import { StatusBar } from 'expo-status-bar';
import { useEffect, useRef, useState, type ReactNode } from 'react';
import { Alert, AppState, Linking, Platform, StyleSheet, Text, View } from 'react-native';
import { SafeAreaProvider, SafeAreaView } from 'react-native-safe-area-context';

import { notificationPermission, onWarningNotificationOpened, showWarningNotification } from './notifications';
import { destinations, retryDelay, sessionResult, type Destination, type Route, type StopReason } from './connection';
import { IncidentHistory } from './history';
import { decodeFrame, decodePairingCode, decodeSavedComputer, requestPairing, type PairedComputer, type SensorFrame } from './protocol';
import { Gpu, Home, Power, Warnings } from './screens';
import { transport } from './transport';
import { BottomNavigation, Button, Card, Notice, Page, Selection, colors, styles, type Tab } from './ui';
import { WarningEvents } from './warningEvents';

const computerKey = 'rtxlens.paired-computer';

type Connection = 'idle' | 'connecting' | 'reconnecting' | 'connected' | 'offline' | 'repair';

export default function App() {
  return (
    <SafeAreaProvider>
      <StatusBar style="light" />
      <SafeAreaView style={styles.screen} edges={['top', 'left', 'right']}>
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
  const [route, setRoute] = useState<Route>('auto');
  const [destination, setDestination] = useState<Destination | null>(null);
  const network = useNetworkState();
  const cellular = network.type === NetworkStateType.CELLULAR;
  const noNetwork = network.type === NetworkStateType.NONE;
  const [active, setActive] = useState(AppState.currentState !== 'background');
  const [connection, setConnection] = useState<Connection>('idle');
  const [frame, setFrame] = useState<SensorFrame | null>(null);
  const [tab, setTab] = useState<Tab>('home');
  const [notifications, setNotifications] = useState<'granted' | 'available' | 'blocked'>('available');
  const [notificationError, setNotificationError] = useState<string | null>(null);
  const [simulations, setSimulations] = useState<SensorFrame['simulationWarnings']>([]);
  const [retry, setRetry] = useState(0);
  const scanReady = useRef(false);
  const pairingJob = useRef<AbortController | null>(null);
  const nextStream = useRef(0);
  const blockedPairing = useRef<StopReason | null>(null);
  const hasReadings = useRef(false);
  const warningEvents = useRef(new WarningEvents());
  const canNotify = useRef(false);

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
    if (!active || !transport) {
      canNotify.current = false;
      return;
    }

    let live = true;
    void notificationPermission().then(value => {
      if (live) {
        canNotify.current = value === 'granted';
        setNotifications(value);
      }
    }).catch(() => {});
    return () => { live = false; };
  }, [active]);

  useEffect(() => {
    if (transport) {
      const subscription = onWarningNotificationOpened(() => setTab('warnings'));
      return () => subscription.remove();
    }
  }, []);

  useEffect(() => {
    if (!transport || !computer || !active) {
      return;
    }

    if (blockedPairing.current) {
      return;
    }
    if (noNetwork) {
      setConnection('offline');
      return;
    }

    const native = transport;
    const { publicKeyFingerprint, token } = computer;
    const addresses = destinations(computer, route, cellular);
    let disposed = false;
    let id: string | null = null;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let connected = false;
    let index = 0;
    let failures = 0;
    let keyMismatch = false;
    let lastSequence = -1;

    function stopAttempt() {
      clearTimeout(timer);
      if (id !== null) {
        const old = id;
        id = null;
        native.closeStream(old);
      }
      connected = false;
    }

    function stopForPairing(reason: StopReason) {
      stopAttempt();
      blockedPairing.current = reason;
      setConnection('repair');
      setError(reason === 'revoked' ? 'The PC no longer authorizes this phone. Pair again in Mobile Access.'
        : 'The PC’s key no longer matches. Verify the PC and pair again in Mobile Access.');
    }

    function failed(mismatch = false) {
      stopAttempt();
      keyMismatch ||= mismatch;
      setConnection('reconnecting');
      if (index < addresses.length) {
        void attempt();
      } else if (keyMismatch) {
        stopForPairing('changed-key');
      } else {
        index = 0;
        timer = setTimeout(() => void attempt(), retryDelay(++failures));
      }
    }

    async function attempt() {
      const target = addresses[index++];
      const candidate = String(++nextStream.current);
      id = candidate;

      try {
        const reply = await native.get(target.address, publicKeyFingerprint, '/session', token);
        if (disposed || id !== candidate) return;
        const result = sessionResult(reply);
        if (result === 'revoked') {
          stopForPairing(result);
          return;
        }
        if (result !== 'connect') {
          failed(result === 'changed-key');
          return;
        }

        lastSequence = -1;
        timer = setTimeout(failed, 15_000);
        setDestination(target);
        native.openStream(candidate, target.address, publicKeyFingerprint, token);
      } catch {
        if (!disposed && id === candidate) failed();
      }
    }

    const states = native.addListener('onStreamState', event => {
      if (event.id !== id) return;
      if (event.reason === 'revoked') stopForPairing('revoked');
      else if (event.state !== 'connected') failed();
      else {
        clearTimeout(timer);
        timer = setTimeout(failed, 45_000);
        connected = true;
        failures = 0;
        keyMismatch = false;
        setConnection('connected');
        setError(null);
      }
    });
    const frames = native.addListener('onFrame', event => {
      if (event.id === id && connected) {
        const decoded = decodeFrame(event.json);
        if (decoded && decoded.sequence > lastSequence) {
          clearTimeout(timer);
          timer = setTimeout(failed, 45_000);
          lastSequence = decoded.sequence;
          hasReadings.current = true;
          setFrame(decoded);
          if (decoded.simulationWarnings.length > 0) {
            setSimulations(previous => {
              const byId = new Map([...previous, ...decoded.simulationWarnings].map(warning => [warning.id, warning]));
              return Array.from(byId.values()).sort((left, right) => Date.parse(right.startedAt) - Date.parse(left.startedAt)).slice(0, 12);
            });
          }

          for (const warning of warningEvents.current.receive(decoded)) {
            if (canNotify.current) {
              const title = warning.simulation ? `Simulation · ${warning.title}` : warning.title;
              void showWarningNotification(title, warning.detail).catch(() => setNotificationError('Could not show a system notification.'));
            }
          }
        }
      }
    });

    setConnection(hasReadings.current ? 'reconnecting' : 'connecting');
    setError(null);
    void attempt();
    return () => {
      disposed = true;
      states.remove();
      frames.remove();
      stopAttempt();
    };
  }, [active, cellular, computer, noNetwork, retry, route]);

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
        setRoute('auto');
        setComputer(paired);
        setTab('home');
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
      setDestination(null);
      setError(null);
      setSimulations([]);
      warningEvents.current = new WarningEvents();
      blockedPairing.current = null;
      hasReadings.current = false;
    } catch {
      setError('The pairing could not be removed from this phone.');
    }
  }

  function navigate(next: Tab) {
    if (next !== 'settings' && scanning) {
      cancelPairing();
    }
    setTab(next);
  }

  async function enableNotifications() {
    setNotificationError(null);
    try {
      const value = await notificationPermission(true);
      canNotify.current = value === 'granted';
      setNotifications(value);
    } catch {
      setNotificationError('Could not enable system notifications.');
    }
  }

  const live = active && connection === 'connected';
  const warningCount = live ? frame?.warnings.filter(warning => warning.phase === 'Active').length ?? 0 : 0;
  let content: ReactNode;

  if (!loaded || !transport) {
    content = <Page title="RTX Lens"><Card title={!loaded ? 'Loading…' : 'Native build required'}>
      {!loaded ? null : <Text style={styles.caption}>Install the RTX Lens IPA or APK to pair with your PC.</Text>}
    </Card></Page>;
  } else if (tab === 'settings') {
    content = (
      <Page title="Settings">
        {error && <Notice>{error}</Notice>}
        {computer ? (
          <Card title="PC connection" icon="link">
            <Text style={[local.state, !live && { color: colors.muted }]}>
              {live ? 'Connected' : connection === 'connecting' ? 'Connecting…' : connection === 'reconnecting' ? 'Reconnecting…'
                : connection === 'repair' ? 'Pair again' : connection === 'offline' ? 'Phone offline' : 'Disconnected'}
            </Text>
            {destination && <Text selectable style={styles.caption}>{destination.address}</Text>}
            <Selection options={[{ id: 'auto', label: 'Automatic' }, { id: 'local', label: 'Home Wi-Fi' }, { id: 'remote', label: 'Away' }]}
              selected={route} onSelect={setRoute} />
            <View style={styles.actions}>
              {(connection === 'reconnecting' || connection === 'offline') && (
                <Button icon="refresh" label="Reconnect" onPress={() => setRetry(value => value + 1)} />
              )}
              <Button icon="close" label="Forget PC" variant="danger" onPress={() => Alert.alert(
                'Forget this PC?', 'To connect again, scan a new pairing code.', [
                  { text: 'Cancel', style: 'cancel' }, { text: 'Forget PC', style: 'destructive', onPress: () => void forgetComputer() },
                ],
              )} />
            </View>
          </Card>
        ) : (
          <Card title="Pair a PC" icon="scan">
            <Text style={styles.caption}>
              On your PC, open Configuration → Mobile Access and create a pairing code. Keep both devices on the same network.
            </Text>
            {scanning && (
              <View style={local.camera}>
                <CameraView style={StyleSheet.absoluteFill} barcodeScannerSettings={{ barcodeTypes: ['qr'] }} onBarcodeScanned={onScan} />
              </View>
            )}
            {pairing && <Text style={local.state}>{pairing}</Text>}
            {scanning || pairing ? (
              pairing !== 'Saving the pairing…' && <Button icon="close" label="Cancel" onPress={cancelPairing} />
            ) : <Button icon="scan" label="Scan pairing code" variant="primary" onPress={startScan} />}
          </Card>
        )}
        <Card title="Warning notifications" icon="warnings">
          <Text style={styles.caption}>Show system notifications for warnings received while RTX Lens is open and connected.</Text>
          {notificationError && <Notice>{notificationError}</Notice>}
          {notifications === 'granted' ? <Text style={local.state}>Enabled</Text> : notifications === 'blocked' ? (
            <Button icon="settings" label="Open system settings" onPress={() => void Linking.openSettings()} />
          ) : <Button icon="warnings" label="Enable notifications" variant="primary" onPress={enableNotifications} />}
        </Card>
      </Page>
    );
  } else if (tab === 'power') {
    content = <Power frame={frame} live={live} />;
  } else if (tab === 'gpu') {
    content = <Gpu frame={frame} live={live} />;
  } else if (tab === 'warnings') {
    content = <Warnings frame={frame} live={live} simulations={simulations}
      history={<IncidentHistory computer={computer} destination={destination} live={live} />} />;
  } else {
    content = <Home frame={frame} live={live} paired={computer !== null} away={destination?.route === 'remote'}
      connecting={connection === 'connecting' || connection === 'reconnecting'} reconnecting={connection === 'reconnecting'} onNavigate={navigate} />;
  }

  return (
    <>
      <View style={styles.screen} key={tab}>{content}</View>
      <SafeAreaView edges={['bottom']} style={styles.bottom}>
        <BottomNavigation selected={tab} warningCount={warningCount} onSelect={navigate} />
      </SafeAreaView>
    </>
  );
}

const local = StyleSheet.create({
  state: { color: colors.accent, fontSize: 15, fontWeight: '700' },
  camera: { height: 255, borderRadius: 14, overflow: 'hidden' },
});
