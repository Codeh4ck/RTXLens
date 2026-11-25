import { mobileProtocol } from './mobileProtocol.ts';
import { useThemeStyles, type Colors } from './appearance';
import { AppearanceSettings } from './appearanceSettings';
import { ThemeContext, decodeAppearance, defaultAppearance, isLight, themeColors, type Appearance } from './appearance';
import { CameraView, useCameraPermissions, type BarcodeScanningResult } from 'expo-camera';
import { useNetworkState, NetworkStateType } from 'expo-network';
import * as SecureStore from 'expo-secure-store';
import { StatusBar } from 'expo-status-bar';
import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { ActivityIndicator, Alert, AppState, Linking, Platform, StyleSheet, Text, View } from 'react-native';
import { SafeAreaProvider, SafeAreaView } from 'react-native-safe-area-context';

import { notificationPermission, onWarningNotificationOpened, showWarningNotification } from './notifications';
import { localSwitchAddress, sessionResult } from './connection';
import { DemoSession } from './demoSession';
import { ConnectionCard } from './connectionCard';
import { IncidentHistory } from './history';
import type { IncidentRef } from './incidentProtocol';
import { useLockScreenNotifications } from './lockScreenNotifications';
import { allowsWarning, type NotificationPreferences } from './mobilePreferences';
import {
  ConnectionAddresses, DisplaySettings, NotificationSettings, TemperatureSettings, ReadingFormatContext, readingFormat, TestRemoteAccess, useMobilePreferences,
} from './mobileSettings';
import { decodePairingCode, decodeSavedComputer, requestPairing, type PairedComputer, type SensorFrame } from './protocol';
import { Gpu, Home, Power, Warnings } from './screens';
import { transport } from './transport';
import { BottomNavigation, Button, Card, Notice, Page, useStyles, type Tab } from './ui';
import { usePcConnection } from './usePcConnection';
import { WarningEvents } from './warningEvents';

const computerKey = 'rtxlens.paired-computer';
const localProbeInterval = 30_000;

const pairingMessages = {
  contacting: 'Contacting your PC…',
  approval: 'Approve the pairing request in RTX Lens on your PC.',
  saving: 'Saving the pairing…',
} as const;

export default function App() {
  const [appearance, setAppearance] = useState(defaultAppearance);
  const [ready, setReady] = useState(false);
  const writes = useRef(Promise.resolve());
  useEffect(() => {
    void SecureStore.getItemAsync('rtxlens.appearance').then(raw => {
      if (raw) setAppearance(decodeAppearance(raw));
    }).catch(() => {}).finally(() => setReady(true));
  }, []);
  const palette = useMemo(() => themeColors(appearance), [appearance]);
  function save(next: Appearance) {
    const pending = writes.current.then(async () => {
      await SecureStore.setItemAsync('rtxlens.appearance', JSON.stringify(next));
      setAppearance(next);
    });
    writes.current = pending.catch(() => {});
    return pending;
  }
  return <ThemeContext.Provider value={palette}><SafeAreaProvider>
    <StatusBar style={isLight(palette.background) ? 'dark' : 'light'} />
    <SafeAreaView style={{ flex: 1, backgroundColor: palette.background }} edges={['top', 'left', 'right']}>
      {ready && <MobilePreview appearance={appearance} saveAppearance={save} />}
    </SafeAreaView>
  </SafeAreaProvider></ThemeContext.Provider>;
}
function MobilePreview({ appearance, saveAppearance }: { appearance: Appearance; saveAppearance: (next: Appearance) => Promise<void> }) {
  const styles = useStyles();
  const local = useThemeStyles(createLocalStyles);
  const [permission, requestPermission] = useCameraPermissions();
  const [computer, setComputer] = useState<PairedComputer | null>(null);
  const [loaded, setLoaded] = useState(false);
  const [scanning, setScanning] = useState(false);
  const [pairing, setPairing] = useState<keyof typeof pairingMessages | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [savedPairingError, setSavedPairingError] = useState<string | null>(null);
  const [openingCamera, setOpeningCamera] = useState(false);
  const [pairingNetworkDenied, setPairingNetworkDenied] = useState(false);
  const network = useNetworkState();
  const wifi = network.type === NetworkStateType.WIFI;
  const [active, setActive] = useState(AppState.currentState !== 'background');
  const [lastReadingsVisible, setLastReadingsVisible] = useState(false);
  const [tab, setTab] = useState<Tab>('home');
  const [demo, setDemo] = useState(false);
  const [editingHome, setEditingHome] = useState(false);
  const [notificationLink, setNotificationLink] = useState<{ index: number; incident: IncidentRef | null } | null>(null);
  const [notifications, setNotifications] = useState<'granted' | 'available' | 'blocked'>('available');
  const [notificationError, setNotificationError] = useState<string | null>(null);
  const [simulations, setSimulations] = useState<SensorFrame['simulationWarnings']>([]);
  const [offerLocalConnection, setOfferLocalConnection] = useState(false);
  const scanReady = useRef(false);
  const cameraRequest = useRef<AbortController | null>(null);
  const pairingJob = useRef<AbortController | null>(null);
  const warningEvents = useRef(new WarningEvents());
  const canNotify = useRef(false);
  const preferences = useMobilePreferences();
  const { notificationRef, loaded: preferencesLoaded } = preferences;
  const { progress, frame, destination, live, retry } = usePcConnection(
    computer, active, network.type, preferencesLoaded, receiveFrame,
  );
  const localAddress = live && wifi && computer ? localSwitchAddress(computer, destination) : null;
  const push = useLockScreenNotifications(computer, destination?.address, live, notificationRef, preferencesLoaded && !preferences.error);
  const canStartPairing = !scanning && pairing === null && !openingCamera && !push.busy;

  useEffect(() => {
    let live = true;
    void SecureStore.getItemAsync(computerKey).then(value => {
      if (live && value) {
        const saved = decodeSavedComputer(value);
        setComputer(saved);
        if (!saved) setSavedPairingError('Your saved pairing couldn’t be restored.');
      }
    }).catch(() => {
      if (live) setSavedPairingError('Your saved pairing couldn’t be read.');
    }).finally(() => {
      if (live) setLoaded(true);
    });

    const subscription = AppState.addEventListener('change', state => setActive(state === 'active'));
    return () => {
      live = false;
      cameraRequest.current?.abort();
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
      const subscription = onWarningNotificationOpened(incident => {
        setNotificationLink(previous => ({ index: (previous?.index ?? 0) + 1, incident }));
        setTab('warnings');
      });
      return () => subscription.remove();
    }
  }, []);

  useEffect(() => {
    if (live) setLastReadingsVisible(false);
  }, [live]);

  function receiveFrame(nextFrame: SensorFrame) {
    if (nextFrame.simulationWarnings.length > 0) {
      setSimulations(previous => {
        const byId = new Map([...previous, ...nextFrame.simulationWarnings].map(warning => [warning.id, warning]));
        return Array.from(byId.values()).sort((left, right) => Date.parse(right.startedAt) - Date.parse(left.startedAt)).slice(0, 12);
      });
    }

    for (const warning of warningEvents.current.receive(nextFrame)) {
      const options = notificationRef.current;
      if (canNotify.current && !push.registered.current && allowsWarning(options, warning.category)) {
        const title = options.showTitle ? (warning.simulation ? `Simulation · ${warning.title}` : warning.title)
          : warning.simulation ? 'RTX Lens simulation' : 'RTX Lens warning';
        const detail = options.showTitle ? warning.detail : 'Open RTX Lens to view the alert.';
        void showWarningNotification(title, detail, warning.incident)
          .catch(() => setNotificationError('Could not show a system notification.'));
      }
    }
  }

  useEffect(() => {
    setOfferLocalConnection(false);
    if (!transport || !computer || !localAddress) return;

    const native = transport;
    const address = localAddress;
    const { publicKeyFingerprint, token } = computer;
    let disposed = false;
    let timer: ReturnType<typeof setTimeout> | undefined;

    async function probe() {
      try {
        const reply = await native.get(address, publicKeyFingerprint, mobileProtocol.Session, token);
        if (disposed) return;
        if (sessionResult(reply) === 'connect') {
          setOfferLocalConnection(true);
          return;
        }
      } catch {
        // The PC can still be reached via its current connection.
      }

      if (!disposed) timer = setTimeout(() => void probe(), localProbeInterval);
    }

    void probe();
    return () => {
      disposed = true;
      clearTimeout(timer);
    };
  }, [computer, localAddress]);

  function openPairing() {
    setTab('settings');
    void startScan();
  }

  async function startScan() {
    if (cameraRequest.current || !canStartPairing) return;
    const request = new AbortController();
    cameraRequest.current = request;
    setOpeningCamera(true);
    setError(null);
    setPairingNetworkDenied(false);
    try {
      const access = permission?.granted ? permission : await requestPermission();
      if (request.signal.aborted) return;
      if (!access.granted) {
        setError('Allow camera access in Settings to scan the pairing code.');
        return;
      }

      scanReady.current = true;
      setScanning(true);
    } catch {
      if (!request.signal.aborted) setError('The camera could not be opened.');
    } finally {
      if (cameraRequest.current === request) {
        cameraRequest.current = null;
        setOpeningCamera(false);
      }
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
      setPairing('contacting');
      const device = Platform.OS === 'ios' ? 'iPhone' : 'Android phone';
      const paired = await requestPairing(code, device,
        (path, json) => native.post(code.localAddress, code.publicKeyFingerprint, path, json),
        () => setPairing('approval'), job.signal);

      if (paired && !job.signal.aborted) {
        setPairing('saving');
        await SecureStore.setItemAsync(computerKey, JSON.stringify(paired), {
          keychainAccessible: SecureStore.WHEN_UNLOCKED_THIS_DEVICE_ONLY,
        });
        if (job.signal.aborted) return;

        const oldAlertsRemoved = !computer || await push.forgetRegistration().catch(() => false);
        if (push.available) await enableNotifications(paired.token);
        if (job.signal.aborted) return;

        setSavedPairingError(null);
        setLastReadingsVisible(false);
        setNotificationLink(null);
        setSimulations([]);
        warningEvents.current = new WarningEvents();
        setComputer(paired);
        setTab('home');
        if (!oldAlertsRemoved) {
          setError('Revoke the previous pairing in Mobile Access on your PC if it is still listed.');
        }
      }
    } catch (failure) {
      if (!job.signal.aborted) {
        if (failure instanceof Error && 'code' in failure && failure.code === 'LOCAL_NETWORK_DENIED') {
          setPairingNetworkDenied(true);
        }
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
    cameraRequest.current?.abort();
    cameraRequest.current = null;
    setOpeningCamera(false);
    pairingJob.current?.abort();
    pairingJob.current = null;
    setPairing(null);
  }

  async function forgetComputer() {
    try {
      const registrationRemoved = await push.forgetRegistration();
      await SecureStore.deleteItemAsync(computerKey);
      setComputer(null);
      setLastReadingsVisible(false);
      setError(registrationRemoved ? null : 'Revoke this phone in Mobile Access on your PC to stop its notifications.');
      setNotificationLink(null);
      setSimulations([]);
      warningEvents.current = new WarningEvents();
    } catch {
      setError('The pairing could not be removed from this phone.');
    }
  }

  function navigate(next: Tab) {
    if (next !== 'settings' && pairing !== 'saving' && (scanning || pairing || openingCamera)) {
      cancelPairing();
    }
    if (next !== tab) setLastReadingsVisible(false);
    if (next !== 'warnings') setNotificationLink(null);
    setTab(next);
  }

  async function enableNotifications(newPairingToken?: string) {
    setNotificationError(null);
    try {
      const value = await notificationPermission(true);
      canNotify.current = value === 'granted';
      setNotifications(value);
      if (value === 'granted' && newPairingToken) {
        await push.saveEnabledPairing(newPairingToken);
      }
    } catch {
      setNotificationError('Could not enable system notifications.');
    }
  }

  async function saveNotificationPreferences(value: NotificationPreferences) {
    await preferences.saveNotifications(value);
    if (push.enabled && live && !await push.applyPreferences(value)) {
      throw new Error('Saved on this phone. Reconnect to sync lock-screen preferences with your PC.');
    }
  }

  const warningCount = live ? frame?.warnings.filter(warning => warning.phase === 'Active').length ?? 0 : 0;
  const showLastReadings = !live && lastReadingsVisible && frame !== null;
  const showTelemetry = live || showLastReadings;
  const hasRemote = computer !== null && computer.localAddress !== computer.remoteAddress;
  const connectionCard = <ConnectionCard progress={progress} savedPairingError={savedPairingError} hasRemote={hasRemote}
    onPair={openPairing} onSystemSettings={() => void Linking.openSettings()}
    onSettings={() => navigate('settings')} onCheck={() => { navigate('settings'); retry(); }}
    onLastReadings={frame && !live ? () => setLastReadingsVisible(visible => !visible) : undefined}
    showingLastReadings={showLastReadings} />;
  const canViewDemo = loaded && !computer && !savedPairingError && canStartPairing;
  if (demo && canViewDemo) {
    return <DemoSession appearance={appearance} active={active} exit={() => { setDemo(false); navigate('home'); }} />;
  }
  const demoButton = canViewDemo && <Button icon="monitor" label="View demo" onPress={() => setDemo(true)} />;
  let content: ReactNode;

  if (!loaded || !transport) {
    content = <Page title="RTX Lens"><Card title={!loaded ? 'Loading…' : 'Native build required'}>
      {!loaded ? null : <Text style={styles.caption}>Install the RTX Lens IPA or APK to pair with your PC.</Text>}
    </Card>{tab === 'home' && demoButton}</Page>;
  } else if (tab === 'settings') {
    content = (
      <Page title="Settings">
        {error && <Notice>{error}</Notice>}
        {pairingNetworkDenied && <Button icon="settings" label="Open system settings" onPress={() => void Linking.openSettings()} />}
        {computer && !scanning && !pairing && !openingCamera ? (
          <ConnectionCard progress={progress} settings hasRemote={hasRemote} onPair={openPairing} onCheck={retry}
            onSettings={() => navigate('settings')} onSystemSettings={() => void Linking.openSettings()}>
            <ConnectionAddresses computer={computer} save={async updated => {
              await SecureStore.setItemAsync(computerKey, JSON.stringify(updated), {
                keychainAccessible: SecureStore.WHEN_UNLOCKED_THIS_DEVICE_ONLY,
              });
              setError(null);
              setComputer(updated);
              retry();
            }} />
            <TestRemoteAccess key={computer.remoteAddress} computer={computer} wifi={wifi} />
            <Button icon="close" label="Forget PC" variant="danger" disabled={push.busy} onPress={() => Alert.alert(
              'Forget this PC?', 'To connect again, scan a new pairing code.', [
                { text: 'Cancel', style: 'cancel' }, { text: 'Forget PC', style: 'destructive', onPress: () => void forgetComputer() },
              ],
            )} />
          </ConnectionCard>
        ) : (
          <Card title={computer || savedPairingError ? 'Pair your PC again' : 'Pair your PC'} icon="scan">
            {savedPairingError && <Notice>{savedPairingError}</Notice>}
            {!pairing && <Text style={styles.caption}>
              Connect both devices to the same network. On your PC, open Configuration → Mobile Access and create a pairing code.
            </Text>}
            {scanning && <View style={local.camera}>
              <CameraView style={StyleSheet.absoluteFill} barcodeScannerSettings={{ barcodeTypes: ['qr'] }} onBarcodeScanned={onScan} />
            </View>}
            {(pairing || openingCamera) && <View style={styles.row} accessibilityLiveRegion="polite">
              <ActivityIndicator size="small" />
              <Text style={[local.state, { flexShrink: 1 }]}>{pairing ? pairingMessages[pairing] : 'Opening the camera…'}</Text>
            </View>}
            {scanning || pairing || openingCamera ? (
              pairing !== 'saving' && <Button icon="close" label="Cancel" onPress={cancelPairing} />
            ) : permission?.canAskAgain === false && !permission.granted ? (
              <Button icon="settings" label="Open system settings" onPress={() => void Linking.openSettings()} />
            ) : <Button icon="scan" label="Scan pairing code" variant="primary" onPress={() => void startScan()} />}
          </Card>
        )}
        <NotificationSettings permission={notifications} permissionError={notificationError} onEnable={() => void enableNotifications()}
          onSystemSettings={() => void Linking.openSettings()} push={push} paired={computer !== null} live={live}
          value={preferencesLoaded ? preferences.notifications : undefined} preferencesError={preferences.error}
          save={saveNotificationPreferences} />
        <AppearanceSettings value={appearance} save={saveAppearance} canEditHome={preferencesLoaded && !preferences.savingDisplay} editHome={() => {
          setEditingHome(true);
          navigate('home');
        }} />
        {preferencesLoaded && <TemperatureSettings unit={preferences.temperature} save={preferences.saveTemperature} />}
        {preferencesLoaded && <DisplaySettings value={preferences.display} busy={preferences.savingDisplay} save={preferences.saveDisplay} />}
      </Page>
    );
  } else if (tab === 'warnings') {
    content = <Warnings key={notificationLink?.index} frame={showTelemetry ? frame : null} live={live} simulations={simulations}
      connectionCard={live ? undefined : connectionCard} showHistory={Boolean(notificationLink?.incident)}
      history={<IncidentHistory computer={computer} destination={destination} live={live} target={notificationLink?.incident}
        onNotificationClosed={() => setNotificationLink(previous => previous && { ...previous, incident: null })} />} />;
  } else if (tab === 'power') {
    content = <Power frame={showTelemetry ? frame : null} live={live} connectionCard={live ? undefined : connectionCard}
      hideIdleMsiConnectors={preferences.display.hideIdleMsiConnectors} />;
  } else if (tab === 'gpu') {
    content = <Gpu frame={showTelemetry ? frame : null} live={live} connectionCard={live ? undefined : connectionCard} />;
  } else if (!showTelemetry && !editingHome) {
    content = <Page title="RTX Lens">{connectionCard}{demoButton}</Page>;
  } else {
    content = <Home frame={frame} live={live} remote={destination?.route === 'remote'} onNavigate={navigate}
      connectionCard={live || editingHome ? undefined : connectionCard}
      panels={preferences.display.homePanels} layoutEnabled={preferencesLoaded} initiallyEditing={editingHome}
      onEditingChange={setEditingHome}
      saveLayout={homePanels => preferences.saveDisplay({ ...preferences.display, homePanels })} />;
  }

  return (
    <>
      {localAddress && offerLocalConnection && !editingHome && <View style={local.connectionPrompt}>
        <Card title="Local connection available" icon="link">
          <Text style={styles.caption}>RTX Lens can reach your PC over this Wi-Fi. Switch to the local connection?</Text>
          <View style={styles.actions}>
            <Button icon="check" label="Switch to local" variant="primary" onPress={() => {
              setOfferLocalConnection(false);
              retry();
            }} />
            <Button icon="close" label="Stay remote" onPress={() => setOfferLocalConnection(false)} />
          </View>
        </Card>
      </View>}
      <ReadingFormatContext.Provider value={readingFormat(preferences.temperature, preferences.display.useGrouping)}>
        <View style={styles.screen} key={tab}>{content}</View>
      </ReadingFormatContext.Provider>
      {tab === 'home' && editingHome ? <SafeAreaView edges={['bottom']} /> :
        <BottomNavigation selected={tab} warningCount={warningCount} onSelect={navigate} />}
    </>
  );
}

const createLocalStyles = (colors: Colors) => StyleSheet.create({
  state: { color: colors.accent, fontSize: 15, fontWeight: '700' },
  camera: { height: 255, borderRadius: 14, overflow: 'hidden' },
  connectionPrompt: { alignSelf: 'center', width: '100%', maxWidth: 680, paddingHorizontal: 20, paddingTop: 12 },
});
