import { useColors, useThemeStyles, type Colors } from './appearance';
import { HomeLayout } from './homeLayout';
import { useContext, useState, type ReactNode } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';

import {
  boostLimitRows, findMetric, formatReading, groupMetrics, isPin, metricLabel, number, pinMetrics, pinPanelSources, pinPanelTitle, powerConnectorGroups,
  sourceName, wireViewFaults, type PinMetric,
} from './metrics';
import { homeCards, homePanelKey, type HomeCardId, type HomePanel } from './mobilePreferences';
import { TemperatureUnitContext } from './mobileSettings';
import type { SensorFrame, SensorReading } from './protocol';
import { Button, Card, Icon, Page, Selection, useHomeLongPress, useStyles, type IconName, type Tab } from './ui';

type TelemetryProps = { frame: SensorFrame | null; live: boolean };

export function Home({
  frame, live, paired, connecting, reconnecting, remote, panels, saveLayout, layoutEnabled, initiallyEditing, onEditingChange, onNavigate,
}: TelemetryProps & {
  paired: boolean; connecting: boolean; reconnecting: boolean; remote: boolean; panels: HomePanel[];
  saveLayout: (panels: HomePanel[]) => Promise<void>; layoutEnabled: boolean; initiallyEditing: boolean; onEditingChange: (editing: boolean) => void;
  onNavigate: (tab: Tab) => void;
}) {
  const styles = useStyles();
  const colors = useColors();
  const local = useThemeStyles(createLocalStyles);
  const temperatureUnit = useContext(TemperatureUnitContext);
  const sensors = frame?.sensors ?? [];
  const gpuGroups = groupMetrics(sensors, 'gpu');
  const gpu = (gpuGroups.find(group => group.source === 'generic-nvidia') ?? gpuGroups[0])?.readings ?? [];
  const powerGroups = groupMetrics(sensors, 'power');
  const pinSources = pinPanelSources(powerGroups);
  const connectorPower = frame?.connectorPowerKey === undefined
    ? powerGroups.map(group => findMetric(group.readings, 'GpuConnector', 'Power')).find(reading => reading !== undefined)
    : sensors.find(reading => reading.identity.key === frame.connectorPowerKey);
  const warningCount = frame?.warnings.filter(warning => warning.phase === 'Active').length ?? 0;
  const tone = live ? warningCount > 0 ? colors.warning : colors.accent : colors.muted;
  const title = !paired ? 'Pair your PC' : !live ? reconnecting ? 'Reconnecting to your PC' : connecting ? 'Connecting to your PC' : 'PC disconnected'
    : !frame ? 'Waiting for readings' : warningCount ? `${warningCount} active ${warningCount === 1 ? 'warning' : 'warnings'}`
      : frame.reportingSources ? 'Monitoring active' : 'No devices reporting';
  const tiles: Record<Exclude<HomeCardId, 'status'>, { value: string; icon: IconName; tab: Tab }> = {
    gpuUsage: { value: formatReading(findMetric(gpu, 'GpuCore', 'Utilization')), icon: 'activity', tab: 'gpu' },
    coreTemperature: {
      value: formatReading(findMetric(gpu, 'GpuCore', 'Temperature'), temperatureUnit), icon: 'temp', tab: 'gpu',
    },
    connectorPower: { value: formatReading(connectorPower), icon: 'power', tab: 'power' },
    activeWarnings: { value: String(warningCount), icon: 'warnings', tab: 'warnings' },
  };

  const status = (
      <View style={[local.hero, { borderColor: live ? warningCount ? colors.warning : colors.secondary : colors.line }]}>
        <View style={styles.row}>
          <View style={[local.statusIcon, { backgroundColor: live && warningCount ? colors.warningSurface : colors.accentSurface }]}>
            <Icon name={live && warningCount ? 'warnings' : 'monitor'} color={tone} size={24} />
          </View>
          <Text style={[local.status, { color: tone }]}>{live ? 'Live' : connecting ? 'Connecting' : paired ? 'Offline' : 'Setup'}</Text>
        </View>
        <Text style={local.heroTitle}>{title}</Text>
        {live && <Text style={styles.caption}>
          {remote ? 'Remote connection' : 'Local connection'}
          {frame && ` · ${frame.reportingSources} reporting ${frame.reportingSources === 1 ? 'source' : 'sources'}`}
        </Text>}
        {!paired && <Button icon="scan" label="Pair a PC" variant="primary" onPress={() => onNavigate('settings')} />}
        {paired && !live && <Button icon="settings" label="Connection settings" onPress={() => onNavigate('settings')} />}
      </View>

  );
  function titleFor(panel: HomePanel) {
    if (panel.kind === 'metric') return homeCards.find(card => card.id === panel.id)?.label ?? panel.id;
    return pinSources.get(homePanelKey(panel))?.title ?? pinPanelTitle(panel);
  }

  const savedPins = panels.filter(panel => panel.kind === 'pins');
  const reportedPins = [...pinSources.values()]
    .filter(source => source.pins.some(pin => pin.quality === 'Valid' && pin.value !== null))
    .map(source => source.panel);
  const pinOptions = new Map([...savedPins, ...reportedPins].map(panel => [homePanelKey(panel), panel]));
  const options = [
    ...homeCards.map(card => ({ panel: { kind: 'metric' as const, id: card.id }, title: card.label })),
    ...[...pinOptions.values()].map(panel => ({ panel, title: titleFor(panel) })),
  ];

  return <HomeLayout panels={panels} options={options} titleFor={titleFor} save={saveLayout}
    enabled={layoutEnabled} initiallyEditing={initiallyEditing} onEditingChange={onEditingChange}
    footer={frame && !live && <Text style={styles.caption}>Last readings at {time(frame.timestamp)}</Text>}
    renderPanel={panel => {
      const panelTitle = titleFor(panel);
      if (panel.kind === 'metric') {
        if (panel.id === 'status') return status;
        const { value, icon, tab } = tiles[panel.id];
        return <Tile title={panelTitle} value={frame ? value : 'Unavailable'} icon={icon} onPress={() => onNavigate(tab)} />;
      }
      const source = pinSources.get(homePanelKey(panel));
      const limit = frame?.pinLimits.find(limit => limit.pluginId === panel.pluginId && limit.deviceId === panel.deviceId);
      return <HomePinChart title={panelTitle} pins={source?.pins} limit={limit?.overcurrentAmperes} />;
    }} />;
}

function Tile({ title, value, icon, onPress }: { title: string; value: string; icon: IconName; onPress: () => void }) {
  const styles = useStyles();
  const colors = useColors();
  const local = useThemeStyles(createLocalStyles);
  const onLongPress = useHomeLongPress();
  return (
    <Pressable accessibilityRole="button" onLongPress={onLongPress} delayLongPress={420} onPress={onPress} style={({ pressed }) => [local.tile, pressed && styles.pressed]}>
      <View style={local.tileIcons}><Icon name={icon} /><Icon name="next" size={14} color={colors.muted} /></View>
      <Text style={styles.caption}>{title}</Text>
      <Text style={[local.tileValue, value === 'Unavailable' && local.unavailable]}>{value}</Text>
    </Pressable>
  );
}

export function Power({ frame, live, hideIdleMsiConnectors }: TelemetryProps & { hideIdleMsiConnectors: boolean }) {
  const local = useThemeStyles(createLocalStyles);
  const [source, setSource] = useState('');
  const [pinMetric, setPinMetric] = useState<PinMetric>('Current');
  const groups = groupMetrics(frame?.sensors ?? [], 'power');
  const selected = groups.find(group => group.key === source) ?? groups[0];
  const connectors = selected ? powerConnectorGroups(selected, hideIdleMsiConnectors) : [];
  const faults = selected?.source === 'wireview-pro2' ? wireViewFaults(selected.readings) : [];

  return (
    <Page title="Power" description={lastReading(frame, live)}>
      {groups.length > 1 && <Selection selected={selected.key}
        options={groups.map(group => ({ id: group.key, label: group.name }))} onSelect={setSource} />}
      {selected ? (
        <>
          {connectors.length === 0 && <Card title="No MSI connectors drawing current" icon="power">{null}</Card>}
          {connectors.map(([id, readings]) => {
            const pins = readings.filter(isPin);
            const totalOrder = ['PowerCapability', 'Power', 'Current', 'Voltage'];
            const totals = readings.filter(reading => ['GpuConnector', 'PsuConnector'].includes(reading.identity.component))
              .sort((a, b) => totalOrder.indexOf(a.identity.metric) - totalOrder.indexOf(b.identity.metric));
            const detail = readings.filter(reading => !isPin(reading) && !totals.includes(reading));
            const limit = frame?.pinLimits.find(limit => limit.pluginId === selected.source
              && limit.deviceId === readings[0]?.identity.deviceId)?.overcurrentAmperes;
            return (
              <View key={id ?? 'default'} style={local.section}>
                {totals.length > 0 && <Card title={id ? `Connector ${id}` : selected.name} icon="power">
                  {totals.map(reading => <Reading key={reading.identity.key} reading={reading} />)}
                </Card>}
                {pins.length > 0 && <PinChart pins={pins} metric={pinMetric} limit={limit} onSelect={setPinMetric} />}
                {detail.length > 0 && <Card title={totals.length ? 'Device readings' : selected.name} icon="monitor">
                  {detail.map(reading => <Reading key={reading.identity.key} reading={reading} />)}
                </Card>}
              </View>
            );
          })}
          {faults.length > 0 && <Card title="WireView faults" icon="warnings">
            {faults.map(fault => <ReadingRow key={fault.name} label={fault.name} value={fault.status} fault />)}
          </Card>}
        </>
      ) : <Empty icon="power" title="No power readings" live={live} />}
    </Page>
  );
}

function HomePinChart({ title, pins, limit }: { title: string; pins?: SensorReading[]; limit?: number }) {
  const styles = useStyles();
  const [metric, setMetric] = useState<PinMetric>('Current');
  return pins ? <PinChart title={title} pins={pins} metric={metric} limit={limit} onSelect={setMetric} /> : (
    <Card title={title} icon="activity">
      <Text style={styles.caption}>No readings from this pin source.</Text>
    </Card>
  );
}

function PinChart({ title = 'Pin distribution', pins, metric, limit, onSelect }: {
  title?: string; pins: SensorReading[]; metric: PinMetric; limit?: number; onSelect: (metric: PinMetric) => void;
}) {
  const styles = useStyles();
  const local = useThemeStyles(createLocalStyles);
  const options = pinMetrics.filter(name => pins.some(pin => pin.identity.metric === name));
  const selected = options.includes(metric) ? metric : options[0];
  if (!selected) {
    return null;
  }

  const readings = pins.filter(pin => pin.identity.metric === selected).sort((a, b) => (a.identity.channel ?? 0) - (b.identity.channel ?? 0));
  const valid = readings.flatMap(reading => reading.value !== null && reading.quality === 'Valid' ? [reading.value] : []);
  const currentLimit = selected === 'Current' ? limit : undefined;
  const ceiling = Math.max(selected === 'Voltage' ? 13 : selected === 'Current' ? 10 : 100, currentLimit ?? 0, ...valid);

  return (
    <Card title={title} icon="activity">
      {options.length > 1 && <Selection options={options.map(id => ({ id, label: id }))} selected={selected} onSelect={onSelect} />}
      {selected === 'Current' && valid.length > 1 && (
        <Text style={styles.caption}>Current spread · {number(Math.max(...valid) - Math.min(...valid), 2)} A</Text>
      )}
      {currentLimit !== undefined && <Text style={styles.caption}>Configured pin limit · {number(currentLimit, 2)} A</Text>}
      <View style={local.chart}>
        {readings.map(reading => {
          const value = reading.quality === 'Valid' ? reading.value : null;
          const pin = `P${reading.identity.channel ?? '?'}`;
          const formatted = value === null ? 'Unavailable' : formatReading(reading);
          const overLimit = currentLimit !== undefined && value !== null && value >= currentLimit;
          return (
            <View key={reading.identity.key} accessible accessibilityLabel={`${pin}, ${formatted}`} style={local.pin}>
              <Text style={local.pinValue} adjustsFontSizeToFit numberOfLines={1}>{value === null ? '–' : formatted}</Text>
              <View style={local.track}>
                {value !== null && <View style={[local.fill, selected === 'Voltage' && local.voltageFill, overLimit && local.overLimit,
                  { height: `${Math.max(0, value / ceiling) * 100}%` }]} />}
              </View>
              <Text style={styles.caption}>{pin}</Text>
            </View>
          );
        })}
      </View>
    </Card>
  );
}

export function Gpu({ frame, live }: TelemetryProps) {
  const styles = useStyles();
  const local = useThemeStyles(createLocalStyles);
  const [source, setSource] = useState('');
  const groups = groupMetrics(frame?.sensors ?? [], 'gpu');
  const selected = groups.find(group => group.key === source) ?? groups.find(group => group.source === 'generic-nvidia') ?? groups[0];
  const readings = selected?.readings ?? [];
  const diagnostics = ['GpuPowerLimits', 'GpuPerformance', 'GpuPcie'];
  const gpuPower = readings.filter(reading => reading.identity.component === 'GpuPowerLimits'
    || (reading.identity.component === 'GpuBoard' && reading.identity.metric === 'Power'));
  const health = readings.filter(reading => !diagnostics.includes(reading.identity.component) && !gpuPower.includes(reading)
    && !['MemoryTotal', 'MemoryFree'].includes(reading.identity.metric));
  const performance = readings.filter(reading => reading.identity.component === 'GpuPerformance'
    && !['SupportedClockReasons', 'ClockLimitDuration'].includes(reading.identity.metric));
  const limits = boostLimitRows(readings);
  const pcie = readings.filter(reading => reading.identity.component === 'GpuPcie');
  const memoryTotal = findMetric(readings, 'GpuMemory', 'MemoryTotal');

  return (
    <Page title="GPU" description={lastReading(frame, live)}>
      {groups.length > 1 && <Selection selected={selected.key}
        options={groups.map(group => ({ id: group.key, label: group.name }))} onSelect={setSource} />}
      {selected ? (
        <>
          {health.length > 0 && <Card title="GPU status" icon="gpu">
            {health.map(reading => (
              <Reading key={reading.identity.key} reading={reading}
                value={reading.identity.metric === 'MemoryUsed' && memoryTotal && reading.quality === 'Valid' && reading.value !== null
                  ? `${formatReading(reading)} / ${formatReading(memoryTotal)}` : undefined} />
            ))}
          </Card>}
          {(gpuPower.length > 0 || performance.length > 0) && <Card title="GPU power status" icon="power">
            {[...performance, ...gpuPower].map(reading => <Reading key={reading.identity.key} reading={reading} />)}
          </Card>}
          {limits.length > 0 && <Card title="GPU boost limits" icon="activity">
            <View style={local.limitRow}>
              <Text style={[styles.caption, local.limitName]}>Reason</Text>
              <Text style={[styles.caption, local.limitStatus]}>Status</Text>
              <Text style={[styles.caption, local.limitTime]}>Time limited</Text>
            </View>
            {limits.map(limit => <View key={limit.name} style={local.limitRow}>
              <Text style={[styles.text, local.limitName]}>{limit.name}</Text>
              <Text style={[limit.active ? local.warningStatus : styles.caption, local.limitStatus]}>
                {limit.active === null ? 'Unknown' : limit.active ? 'Active' : 'Idle'}
              </Text>
              <Text style={[styles.caption, local.limitTime]}>{limit.seconds === null ? 'Unavailable' : `${number(limit.seconds, 3)} s`}</Text>
            </View>)}
          </Card>}
          {pcie.length > 0 && <Card title="GPU PCIe status" icon="activity">
            <ReadingRow label="PCIe Link Speed" value={linkSpeed(pcie, 'LinkGeneration', 'LinkWidth')} />
            <ReadingRow label="Max PCIe Link Speed" value={linkSpeed(pcie, 'MaximumSystemLinkGeneration', 'MaximumLinkWidth')} />
            {pcie.filter(reading => !['LinkGeneration', 'LinkWidth', 'LinkSpeed', 'MaximumSystemLinkGeneration', 'MaximumLinkWidth',
              'MaximumLinkSpeed'].includes(reading.identity.metric)).map(reading => <Reading key={reading.identity.key} reading={reading}
                value={reading.identity.metric === 'MaximumGpuLinkGeneration' && reading.quality === 'Valid' && reading.value !== null
                  ? `Gen ${reading.value}` : undefined} />)}
          </Card>}
        </>
      ) : <Empty icon="gpu" title="No GPU readings" live={live} />}
    </Page>
  );
}

function linkSpeed(readings: SensorReading[], genMetric: string, widthMetric: string) {
  const generation = findMetric(readings, 'GpuPcie', genMetric)?.value;
  const width = findMetric(readings, 'GpuPcie', widthMetric)?.value;
  const rate = generation ? [0, 2.5, 5, 8, 16, 32, 64, 128][generation] : undefined;
  return [generation && `Gen ${generation}`, width && `×${width}`, rate && `· ${number(rate, 1)} GT/s per lane`]
    .filter(Boolean).join(' ') || 'Unavailable';
}

export function Warnings({ frame, live, simulations, history, showHistory }: TelemetryProps & {
  simulations: SensorFrame['simulationWarnings']; history: ReactNode; showHistory?: boolean;
}) {
  const styles = useStyles();
  const [view, setView] = useState<'active' | 'history'>(showHistory ? 'history' : 'active');
  const warnings = [...frame?.warnings ?? []].sort((a, b) => (b.startedAt ?? '').localeCompare(a.startedAt ?? ''));

  return (
    <Page title="Warnings" description={lastReading(frame, live)}>
      <Selection options={[{ id: 'active', label: 'Active' }, { id: 'history', label: 'History' }]} selected={view} onSelect={setView} />
      {view === 'history' ? history : <>
      {warnings.length === 0 && (
        <Card title={live && frame ? 'No active warnings' : 'Warning status unavailable'} icon={live && frame ? 'check' : 'warnings'}>
          <Text style={styles.caption}>{live && frame ? 'The connected service reports no current warnings.' : 'Waiting for the service.'}</Text>
        </Card>
      )}
      {warnings.map(warning => <WarningCard key={warning.episodeId} title={warning.title} detail={warning.detail}
        metadata={[sourceName(warning.pluginId), warning.startedAt && time(warning.startedAt)].filter(Boolean).join(' · ')}
        status={warning.phase} />)}
      {simulations.length > 0 && <Text accessibilityRole="header" style={styles.heading}>Simulation</Text>}
      {simulations.map(warning => <WarningCard key={warning.id} title={warning.title} detail={warning.detail}
        metadata={time(warning.startedAt)} status="Simulation" />)}
      </>}
    </Page>
  );
}

function WarningCard({ title, detail, metadata, status }: { title: string; detail: string; metadata: string; status: string }) {
  const styles = useStyles();
  const colors = useColors();
  const local = useThemeStyles(createLocalStyles);
  return (
    <Card>
      <View style={styles.row}>
        <Icon name="warnings" color={colors.warning} />
        <Text style={local.warningStatus}>{status}</Text>
      </View>
      <Text style={styles.heading}>{title}</Text>
      {detail && <Text style={styles.text}>{detail}</Text>}
      <Text style={styles.caption}>{metadata}</Text>
    </Card>
  );
}

function Empty({ icon, title, live }: { icon: IconName; title: string; live: boolean }) {
  const styles = useStyles();
  return <Card title={title} icon={icon}>
    <Text style={styles.caption}>{live ? 'The connected service has no readings for this view yet.' : 'Connect to your PC to see live readings.'}</Text>
  </Card>;
}

function Reading({ reading, value }: { reading: SensorReading; value?: string }) {
  const temperatureUnit = useContext(TemperatureUnitContext);
  const fault = (reading.identity.metric === 'FaultLog' || reading.identity.metric === 'FaultStatus')
    && reading.quality === 'Valid' && reading.value !== null && reading.value !== 0;
  return <ReadingRow label={metricLabel(reading)} value={value ?? formatReading(reading, temperatureUnit)} fault={fault} />;
}

export function ReadingRow({ label, value, fault = false }: { label: string; value: string; fault?: boolean }) {
  const styles = useStyles();
  const local = useThemeStyles(createLocalStyles);
  return (
    <View style={[local.reading, fault && local.fault]}>
      <Text style={[styles.text, local.readingLabel]}>{label}</Text>
      <Text style={[local.readingValue, value === 'Unavailable' && local.unavailable]}>{value}</Text>
    </View>
  );
}

function lastReading(frame: SensorFrame | null, live: boolean) {
  return frame && !live ? `Last readings at ${time(frame.timestamp)}` : undefined;
}

function time(date: string) {
  return new Date(date).toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' });
}

const createLocalStyles = (colors: Colors) => StyleSheet.create({
  hero: { backgroundColor: colors.accentSurface, borderWidth: 1, borderRadius: 25, padding: 19, gap: 12, alignItems: 'flex-start' },
  statusIcon: { padding: 10, borderRadius: 14 },
  status: { fontSize: 14, fontWeight: '700' },
  heroTitle: { fontSize: 25, fontWeight: '700', color: colors.text, letterSpacing: -0.4 },
  tile: { padding: 14, borderRadius: 18, borderWidth: 1, borderColor: colors.line,
    backgroundColor: colors.surface, gap: 7 },
  tileIcons: { flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center', marginBottom: 4 },
  tileValue: { color: colors.text, fontWeight: '700', fontSize: 22, fontVariant: ['tabular-nums'] },
  unavailable: { color: colors.muted, fontSize: 13 },
  section: { gap: 12 },
  reading: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', gap: 12, minHeight: 38, paddingHorizontal: 8,
    borderBottomWidth: StyleSheet.hairlineWidth, borderColor: colors.line, paddingBottom: 7 },
  readingLabel: { flex: 1 },
  readingValue: { color: colors.text, flexShrink: 1, maxWidth: '55%', textAlign: 'right', fontWeight: '700', fontVariant: ['tabular-nums'] },
  fault: { backgroundColor: colors.dangerSurface, borderColor: colors.danger, borderWidth: 1, borderRadius: 10, paddingTop: 7 },
  chart: { flexDirection: 'row', gap: 6, marginTop: 5 },
  pin: { flex: 1, alignItems: 'center', gap: 6 },
  pinValue: { color: colors.text, fontSize: 12, fontWeight: '700', width: '100%', textAlign: 'center', fontVariant: ['tabular-nums'] },
  track: { backgroundColor: colors.raised, width: '66%', height: 104, borderRadius: 6, overflow: 'hidden', justifyContent: 'flex-end' },
  fill: { backgroundColor: colors.secondary, borderRadius: 4 },
  voltageFill: { backgroundColor: colors.tertiary },
  overLimit: { backgroundColor: colors.danger },
  warningStatus: { color: colors.warning, fontSize: 13, fontWeight: '700' },
  limitRow: { flexDirection: 'row', alignItems: 'center', gap: 6, paddingBottom: 8,
    borderBottomWidth: StyleSheet.hairlineWidth, borderColor: colors.line },
  limitName: { flex: 1 },
  limitStatus: { width: 58, textAlign: 'right' },
  limitTime: { width: 84, textAlign: 'right', fontVariant: ['tabular-nums'] },
});
