import type { ReactNode } from 'react';
import { ActivityIndicator, Text, View } from 'react-native';

import { useColors } from './appearance';
import type { ConnectionProgress, ConnectionStage, Destination } from './connection';
import { Button, Card, PanelList, useStyles, type IconName } from './ui';

const routeNames = { local: 'Local network', remote: 'Remote access' } as const;

const stages: Record<ConnectionStage, { title: string; detail: string; check: string; icon: IconName }> = {
  idle: { title: 'Pair your PC',
    detail: 'Connect both devices to the same network. On your PC, open Configuration → Mobile Access and create a pairing code.',
    check: 'Ready to pair', icon: 'scan' },
  offline: { title: 'Your phone is offline',
    detail: 'Connect to Wi-Fi or mobile data. RTX Lens will try again when your network comes back.',
    check: 'Phone offline', icon: 'away' },
  checking: { title: 'Connecting to your PC',
    detail: 'Checking your saved connection addresses.', check: 'Checking address', icon: 'monitor' },
  'opening-stream': { title: 'Opening live readings',
    detail: 'Mobile Access responded. RTX Lens is opening the readings stream.',
    check: 'Opening stream', icon: 'activity' },
  'waiting-for-readings': { title: 'Waiting for readings',
    detail: 'The stream is open. The dashboard will appear when the first readings arrive.',
    check: 'Waiting for readings', icon: 'activity' },
  live: { title: 'PC connected',
    detail: '', check: 'Receiving readings', icon: 'check' },
  unreachable: { title: 'Can’t reach your PC',
    detail: 'Check that the PC is awake, the RTX Lens service is running, and Mobile Access is enabled.',
    check: 'No response', icon: 'link' },
  'local-network-denied': { title: 'Allow local network access',
    detail: 'Enable Local Network for RTX Lens in iPhone Settings to connect to your PC over Wi-Fi.',
    check: 'Local network access blocked', icon: 'shield' },
  'changed-key': { title: 'Verify your PC’s address',
    detail: 'The address responded with a different security key. Check the saved addresses against Mobile Access on your PC.',
    check: 'PC key did not match', icon: 'shield' },
  revoked: { title: 'Pair your PC again',
    detail: 'Your PC no longer accepts this pairing. Create a new code under Configuration → Mobile Access.',
    check: 'Pairing rejected', icon: 'scan' },
  'stream-unavailable': { title: 'Can’t open live readings',
    detail: 'Mobile Access responded, but the readings stream didn’t open. Check the RTX Lens service and try again.',
    check: 'Stream unavailable', icon: 'activity' },
  'no-readings': { title: 'PC connected · No readings yet',
    detail: 'Check on the PC that RTX Lens is receiving readings from its plugins.',
    check: 'No readings received', icon: 'activity' },
  incompatible: { title: 'Can’t read data from your PC',
    detail: 'Check for RTX Lens updates on both your phone and your PC.',
    check: 'Readings could not be decoded', icon: 'warnings' },
};

export function ConnectionCard({ progress, savedPairingError, settings, hasRemote, onPair, onCheck, onSettings, onSystemSettings,
  onLastReadings, showingLastReadings = false, children }: {
  progress: ConnectionProgress;
  savedPairingError?: string | null;
  settings?: boolean;
  hasRemote: boolean;
  onPair: () => void;
  onCheck: () => void;
  onSettings: () => void;
  onSystemSettings: () => void;
  onLastReadings?: () => void;
  showingLastReadings?: boolean;
  children?: ReactNode;
}) {
  const styles = useStyles();
  const colors = useColors();
  const { stage, route, checks } = progress;
  const info = stages[stage];
  const busy = stage === 'checking' || stage === 'opening-stream' || stage === 'waiting-for-readings';
  const pairingNeeded = stage === 'idle' || stage === 'revoked';
  const routes: Destination['route'][] = hasRemote ? ['local', 'remote'] : ['local'];
  const showChecks = settings && stage !== 'idle' && routes.some(candidate => checks[candidate] !== undefined);
  const showSettings = !settings && (busy || stage === 'offline' || stage === 'local-network-denied');
  const detail = stage === 'unreachable' && !hasRemote ? 'Connect to the same network as your PC and check that Mobile Access is enabled.'
    : info.detail;

  let action: ReactNode;
  if (pairingNeeded) {
    action = <Button icon="scan" label={stage === 'revoked' || savedPairingError ? 'Pair again' : 'Scan pairing code'}
      variant="primary" onPress={onPair} />;
  } else if (stage === 'local-network-denied') {
    action = <Button icon="settings" label="Open system settings" variant="primary" onPress={onSystemSettings} />;
  } else if (stage === 'changed-key') {
    action = settings ? <Button icon="scan" label="Pair again" onPress={onPair} />
      : <Button icon="settings" label="Check addresses" variant="primary" onPress={onSettings} />;
  } else if (stage !== 'live' && !busy && stage !== 'offline') {
    action = <Button icon="refresh" label={settings ? 'Retry now' : 'Check connection'} variant="primary" onPress={onCheck} />;
  }

  return (
    <Card title={stage === 'idle' && savedPairingError ? 'Pair your PC again' : info.title} icon={info.icon}>
      <View accessibilityLiveRegion="polite" style={styles.row}>
        {busy && <ActivityIndicator size="small" color={colors.accent} />}
        <Text style={[styles.caption, { flexShrink: 1 }]}>
          {savedPairingError ?? (stage === 'live' && route ? `Receiving readings via ${routeNames[route].toLowerCase()}.`
            : busy && route ? `${routeNames[route]} · ${info.check}` : detail)}
        </Text>
      </View>
      {showChecks && <PanelList>{routes.map(candidate => (
        <View key={candidate} style={[styles.row, { justifyContent: 'space-between', paddingVertical: 8 }]}>
          <Text style={styles.text}>{routeNames[candidate]}</Text>
          <Text style={[styles.caption, { flexShrink: 1, textAlign: 'right' }]}>
            {checks[candidate] ? stages[checks[candidate]].check : 'Not checked'}
          </Text>
        </View>
      ))}</PanelList>}
      {action}
      {stage === 'idle' && savedPairingError && <Text style={styles.caption}>{info.detail}</Text>}
      {showSettings &&
        <Button icon="settings" label="Connection settings" onPress={onSettings} />}
      {onLastReadings && <Button icon="clock" label={showingLastReadings ? 'Hide last readings' : 'View last readings'}
        onPress={onLastReadings} />}
      {children}
    </Card>
  );
}
