import { useCallback, useContext, useEffect, useRef, useState } from 'react';
import { Modal, Pressable, StyleSheet, Text, View } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';

import type { Destination } from './connection';
import { decodeIncident, decodeIncidentPage, type Incident, type IncidentRef, type IncidentSummary } from './incidentProtocol';
import { formatReading, isPin, metricLabel, sourceName } from './metrics';
import { TemperatureUnitContext } from './mobileSettings';
import type { PairedComputer, SensorReading } from './protocol';
import { ReadingRow } from './screens';
import { transport } from './transport';
import { Button, Card, Icon, Notice, Page, colors, styles } from './ui';

export function IncidentHistory({ computer, destination, live, target, onNotificationClosed }: {
  computer: PairedComputer | null; destination: Destination | null; live: boolean;
  target?: IncidentRef | null; onNotificationClosed?: () => void;
}) {
  const [items, setItems] = useState<IncidentSummary[]>([]);
  const [next, setNext] = useState<string | null>(null);
  const [selected, setSelected] = useState<IncidentRef | null>(target ?? null);
  const [detail, setDetail] = useState<Incident | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [reload, setReload] = useState(0);
  const generation = useRef(0);
  const inFlight = useRef(false);
  const address = destination?.address;

  const request = useCallback(async (path: string) => {
    if (!transport || !computer || !address || !live) {
      throw new Error('Connect to your PC to load history.');
    }

    const response = await transport.get(address, computer.publicKeyFingerprint, path, computer.token);
    if (response.keyMismatch || response.status === 401) {
      throw new Error('Pair with your PC again to load history.');
    }
    if (response.status === 404) {
      throw new Error(path === '/incidents' ? 'Update the RTX Lens service to view history.' : 'This report is no longer available.');
    }
    if (response.status !== 200) {
      throw new Error('The PC could not load this report. Try again.');
    }
    return response.body;
  }, [address, computer, live]);

  const loadPage = useCallback(async (cursor: string | null) => {
    if (inFlight.current) return;
    const current = ++generation.current;
    inFlight.current = true;
    setBusy(true);
    setError(null);

    try {
      const page = decodeIncidentPage(await request(cursor ? `/incidents?before=${cursor}` : '/incidents'));
      if (generation.current !== current) return;
      setItems(previous => cursor ? [...new Map([...previous, ...page.items].map(item => [entryKey(item), item])).values()] : page.items);
      setNext(page.next);
    } catch (failure) {
      if (generation.current === current) setError(message(failure));
    } finally {
      if (generation.current === current) {
        inFlight.current = false;
        setBusy(false);
      }
    }
  }, [request]);

  useEffect(() => {
    if (live && selected === null) void loadPage(null);
    return () => {
      generation.current++;
      inFlight.current = false;
    };
  }, [live, loadPage, reload, selected]);

  useEffect(() => {
    if (!selected || !live) return;
    const current = ++generation.current;
    inFlight.current = true;
    setBusy(true);
    setError(null);
    void request(`/incidents/${selected.kind}/${selected.id}`).then(body => {
      if (generation.current === current) setDetail(decodeIncident(body, selected));
    }).catch(failure => {
      if (generation.current === current) setError(message(failure));
    }).finally(() => {
      if (generation.current === current) {
        inFlight.current = false;
        setBusy(false);
      }
    });
  }, [live, reload, request, selected]);

  if (!live) {
    return <Card title="History unavailable" icon="warnings">
      <Text style={styles.caption}>Connect to your PC to load its saved incidents.</Text>
    </Card>;
  }

  function closeReport() {
    setSelected(null);
    setDetail(null);
    if (target) onNotificationClosed?.();
  }

  return (
    <>
      <Button icon="refresh" label="Refresh history" onPress={() => setReload(value => value + 1)} />
      {error && <Notice>{error}</Notice>}
      {items.length > 0 && <Card title="Recent incidents" icon="warnings">
        {items.map(item => <Pressable key={entryKey(item)} accessibilityRole="button" disabled={busy}
          onPress={() => setSelected(item)} style={local.entry}>
          <View style={local.entryText}>
            <Text style={styles.text}>{item.title}</Text>
            <Text style={styles.caption}>{sourceName(item.source)} · {time(item.startedAt)}</Text>
            <Text style={styles.caption}>{item.status}</Text>
          </View>
          <Icon name="next" size={17} />
        </Pressable>)}
      </Card>}
      {busy && <Text style={styles.caption}>Loading history…</Text>}
      {!busy && !error && items.length === 0 && <Text style={styles.caption}>No incidents in the configured retention period.</Text>}
      {next && !busy && <Button icon="next" label="Load older incidents" onPress={() => void loadPage(next)} />}
      {selected && <Modal animationType="slide" onRequestClose={closeReport}>
        <SafeAreaView style={styles.screen}>
          <Page title="Incident report">
            <Button icon="back" label="Back to history" onPress={closeReport} />
            {busy && <Text style={styles.caption}>Loading report…</Text>}
            {error && <Notice>{error}</Notice>}
            {error && <Button icon="refresh" label="Retry report" onPress={() => setReload(value => value + 1)} />}
            {detail && <IncidentDetail incident={detail} />}
          </Page>
        </SafeAreaView>
      </Modal>}
    </>
  );
}

function IncidentDetail({ incident }: { incident: Incident }) {
  const temperatureUnit = useContext(TemperatureUnitContext);
  const groups = new Map<string, { name: string; readings: SensorReading[] }>();
  for (const reading of incident.readings) {
    const { pluginId, deviceId, connectorId } = reading.identity;
    const key = JSON.stringify([pluginId, deviceId, connectorId]);
    const group = groups.get(key) ?? {
      name: sourceName(pluginId) + (connectorId ? ` · Connector ${connectorId}` : ''), readings: [],
    };
    group.readings.push(reading);
    groups.set(key, group);
  }

  return (
    <>
      <Card title={incident.title} icon="warnings">
        <Text style={styles.caption}>{sourceName(incident.source)}</Text>
        {incident.detail && <Text style={styles.text}>{incident.detail}</Text>}
        <ReadingRow label="Status" value={incident.status} />
        <ReadingRow label="Triggered" value={time(incident.startedAt)} />
        {incident.endedAt && <ReadingRow label="Ended" value={time(incident.endedAt)} />}
        {incident.deviceId && <ReadingRow label="Device" value={incident.deviceId} />}
        {incident.connector !== null && <ReadingRow label="Connector" value={String(incident.connector)} />}
        {incident.delayMs !== null && <ReadingRow label="Shutdown delay" value={`${incident.delayMs.toLocaleString()} ms`} />}
        {incident.deliveryStatus && <ReadingRow label="Email status" value={incident.deliveryStatus} />}
      </Card>
      {incident.steps.length > 0 && <Card title="Action steps" icon="activity">
        {incident.steps.map((step, index) => <View key={index} style={local.entry}>
          <View style={local.entryText}>
            <Text style={styles.text}>{step.name}</Text>
            <Text style={styles.caption}>{[step.timestamp && time(step.timestamp), step.result,
              step.exitCode !== null && `Exit code ${step.exitCode}`].filter(Boolean).join(' · ')}</Text>
          </View>
        </View>)}
      </Card>}
      <Text accessibilityRole="header" style={styles.heading}>Sensor snapshot</Text>
      {groups.size === 0 && <Text style={styles.caption}>No readings were captured for this incident.</Text>}
      {groups.size > 0 && incident.capturedAt && <Text style={styles.caption}>{time(incident.capturedAt)}</Text>}
      {Array.from(groups, ([key, group]) => <Card key={key} title={group.name} icon="activity">
        {group.readings.map(reading => {
          const label = metricLabel(reading);
          return <ReadingRow key={reading.identity.key} value={formatReading(reading, temperatureUnit)}
            label={isPin(reading) ? `P${reading.identity.channel ?? '?'} ${label.toLowerCase()}` : label} />;
        })}
      </Card>)}
    </>
  );
}

function entryKey(item: IncidentSummary) {
  return `${item.kind}/${item.id}`;
}

function time(value: string) {
  return new Date(value).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'medium' });
}

function message(error: unknown) {
  return error instanceof Error ? error.message : 'Could not load the PC’s warning history.';
}

const local = StyleSheet.create({
  entry: { flexDirection: 'row', alignItems: 'center', gap: 12, paddingVertical: 8, borderBottomWidth: StyleSheet.hairlineWidth,
    borderColor: colors.line },
  entryText: { flex: 1, gap: 3 },
});
