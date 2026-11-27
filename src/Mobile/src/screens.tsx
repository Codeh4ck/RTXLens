import { BoardPower, ConnectorSummary, GpuOverview, LinkLanes, MetricGrid } from './gpuInstruments';
import { AsusId, NvidiaId, WireViewId } from './pluginIds.ts';
import { useColors, useThemeStyles, type Colors } from './appearance';
import { HomeLayout } from './homeLayout';
import { useContext, useState, type ReactNode } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import {
  boostLimitRows, findMetric, groupMetrics, hardwareTabGroups, isPin, metricLabel, pinCurrentSummary, pinMetrics, pinPanelSources, pinPanelTitle, powerConnectorGroups,
  gpuConnectorPower, gpuDeviceGroups, gpuStatusRows, isGpuPowerReading, sourceName, wireViewFaults, type PinMetric,
} from './metrics';
import { homeCards, homePanelKey, type HomeCardId, type HomePanel } from './mobilePreferences';
import { ReadingFormatContext } from './mobileSettings';
import type { SensorFrame, SensorReading } from './protocol';
import { ActiveWarningActions, type WarningActions } from './warningActions';
import { Card, Icon, Page, PanelList, Selection, useStyles, type IconName, type Tab } from './ui';

type TelemetryProps = { frame: SensorFrame | null; live: boolean; connectionCard?: ReactNode };

export function Home({
  frame, live, connectionCard, remote, demo = false, panels, saveLayout, layoutEnabled, initiallyEditing, onEditingChange, onNavigate,
}: TelemetryProps & {
  remote: boolean; demo?: boolean; panels: HomePanel[];
  saveLayout: (panels: HomePanel[]) => Promise<void>; layoutEnabled: boolean; initiallyEditing: boolean; onEditingChange: (editing: boolean) => void;
  onNavigate: (tab: Tab) => void;
}) {
  const styles = useStyles();
  const colors = useColors();
  const local = useThemeStyles(createLocalStyles);
  const { formatReading } = useContext(ReadingFormatContext);
  const sensors = frame?.sensors ?? [];
  const gpuGroups = groupMetrics(sensors, 'gpu');
  const gpu = (gpuGroups.find(group => group.source === NvidiaId) ?? gpuGroups[0])?.readings ?? [];
  const powerGroups = groupMetrics(sensors, 'power');
  const pinSources = pinPanelSources(powerGroups);

  const connectorPower = frame?.connectorPowerKey === undefined
    ? powerGroups.map(group => findMetric(group.readings, 'GpuConnector', 'Power')).find(reading => reading !== undefined)
    : sensors.find(reading => reading.identity.key === frame.connectorPowerKey);

  const warningCount = frame?.warnings.filter(warning => warning.phase === 'Active').length ?? 0;
  const tone = live ? warningCount > 0 ? colors.warning : colors.accent : colors.muted;
  const title = !live ? 'Last reported status' : warningCount ? `${warningCount} active ${warningCount === 1 ? 'warning' : 'warnings'}` : frame?.reportingSources ? 'Monitoring active' : 'No devices reporting';
 
  const tiles: Record<Exclude<HomeCardId, 'status'>, { value: string; icon: IconName; tab: Tab }> = {
    gpuUsage: { value: formatReading(findMetric(gpu, 'GpuCore', 'Utilization')), icon: 'activity', tab: 'gpu' },
    coreTemperature: { value: formatReading(findMetric(gpu, 'GpuCore', 'Temperature')), icon: 'temp', tab: 'gpu' },
    connectorPower: { value: formatReading(connectorPower), icon: 'power', tab: 'power' },
    activeWarnings: { value: String(warningCount), icon: 'warnings', tab: 'warnings' },
  };

  const status = (
    <Card title={title} icon={live && warningCount ? 'warnings' : 'monitor'}>
      <View style={styles.row}>
        <View style={[local.statusDot, { backgroundColor: tone }]} />
        <Text style={[local.status, { color: tone }]}>{demo ? 'Demo' : live ? 'Live' : 'Offline'}</Text>
      </View>
      {live && <Text style={styles.caption}>
        {demo ? 'Simulated devices' : remote ? 'Remote connection' : 'Local connection'}
        {frame && ` · ${frame.reportingSources} reporting ${frame.reportingSources === 1 ? 'source' : 'sources'}`}
      </Text>}

    </Card>
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

    header={<>{connectionCard}{frame && !live && <Text style={styles.caption}>{lastReading(frame, live)}</Text>}</>}
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
  const local = useThemeStyles(createLocalStyles);
  return (
    <Card title={title} icon={icon} compact onPress={onPress}>
      <Text style={[local.tileValue, value === 'Unavailable' && local.unavailable]}>{value}</Text>
    </Card>
  );
}

export function Power({ frame, live, connectionCard, hideIdleMsiConnectors }: TelemetryProps & { hideIdleMsiConnectors: boolean }) {
  const local = useThemeStyles(createLocalStyles);
  const [source, setSource] = useState('');
  const [pinMetric, setPinMetric] = useState<PinMetric>('Current');

  if (!frame) return <Page title="Power">{connectionCard}</Page>;

  const groups = hardwareTabGroups(frame.sensors, 'power');
  const selected = groups.find(group => group.key === source) ?? groups[0];
  const connectors = selected ? powerConnectorGroups(selected, hideIdleMsiConnectors) : [];
  const faults = selected?.source === WireViewId ? wireViewFaults(selected.readings) : [];

  return (
    <Page title="Power" description={lastReading(frame, live)}>
      {connectionCard}
      {groups.length > 1 && <Selection selected={selected.key}
        options={groups.map(group => ({ id: group.key, label: group.name }))} onSelect={setSource} />}
      {selected ? (
        <>
          {connectors.length === 0 && <Card title="No MSI connectors drawing current" icon="power">{null}</Card>}
          {connectors.map(([id, sourceReadings]) => {
            const readings = selected.source === AsusId ? sourceReadings.filter(r => r.identity.metric !== 'ImbalanceDetected') : sourceReadings;
            const pins = readings.filter(isPin);
            const totals = readings.filter(reading => ['GpuConnector', 'PsuConnector'].includes(reading.identity.component));
            const detail = readings.filter(reading => !isPin(reading) && !totals.includes(reading));
            const limit = frame?.pinLimits.find(limit => limit.pluginId === selected.source && limit.deviceId === readings[0]?.identity.deviceId)?.overcurrentAmperes;

            return (
              <View key={id ?? 'default'} style={local.section}>
                {totals.length > 0 && <Card title={id ? `Connector ${id}` : selected.name} icon="power">
                  <ConnectorSummary readings={totals} />
                </Card>}
                {pins.length > 0 && <PinChart pins={pins} metric={pinMetric} limit={limit} onSelect={setPinMetric} />}
                {detail.length > 0 && <Card title={totals.length ? 'Device readings' : selected.name} icon="monitor">
                  <PanelList>{detail.map(reading => <Reading key={reading.identity.key} reading={reading} />)}</PanelList>
                </Card>}
              </View>
            );
          })}
          {faults.length > 0 && <Card title="WireView faults" icon="warnings">
            <PanelList>{faults.map(fault => <ReadingRow key={fault.name} label={fault.name} value={fault.status} icon="warnings" fault />)}</PanelList>
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
  const colors = useColors();
  const local = useThemeStyles(createLocalStyles);

  const { number, formatReading } = useContext(ReadingFormatContext);
  const options = pinMetrics.filter(name => pins.some(pin => pin.identity.metric === name));
  const selected = options.includes(metric) ? metric : options[0];

  if (!selected) {
    return null;
  }

  const readings = pins.filter(pin => pin.identity.metric === selected).sort((a, b) => (a.identity.channel ?? 0) - (b.identity.channel ?? 0));
  const valid = readings.flatMap(reading => reading.value !== null && reading.quality === 'Valid' ? [reading.value] : []);
  const currentLimit = selected === 'Current' ? limit : undefined;
  const summary = selected === 'Current' ? pinCurrentSummary(readings) : null;
  const ceiling = Math.max(selected === 'Voltage' ? 13 : selected === 'Current' ? 10 : 100, currentLimit ?? 0, ...valid);

  return (
    <Card title={title} icon="activity">
      {options.length > 1 && <Selection options={options.map(id => ({ id, label: id }))} selected={selected} onSelect={onSelect} />}
      {summary && <View style={local.pinSummary}>
        <PinStat label="Delta" description="Current spread" value={`${number(summary.delta, 2)} A`} icon="delta" />
        <PinStat label="PMax" description="Highest-current pin"
          value={`P${summary.high.channel ?? '?'} · ${number(summary.high.value, 2)} A`} icon="up" />
        <PinStat label="PLow" description="Lowest-current pin"
          value={`P${summary.low.channel ?? '?'} · ${number(summary.low.value, 2)} A`} icon="down" />
      </View>}
      {currentLimit !== undefined && <View style={local.pinLimit} accessible
        accessibilityLabel={`Configured pin limit, ${number(currentLimit, 2)} amperes`}>
        <Icon name="shield" size={14} color={colors.secondary} />
        <Text style={styles.caption}>Pin limit</Text><Text style={local.statValue}>{number(currentLimit, 2)} A</Text>
      </View>}
      <View style={local.chart}>
        {readings.map(reading => {
          const value = reading.quality === 'Valid' ? reading.value : null;
          const pin = `P${reading.identity.channel ?? '?'}`;
          const formatted = value === null ? 'Unavailable' : formatReading(reading);
          const overLimit = currentLimit !== undefined && value !== null && value >= currentLimit;
          return (
            <View key={reading.identity.key} accessible accessibilityLabel={`${pin}, ${formatted}`} style={local.pin}>
              <Text style={[local.pinValue, overLimit && local.exceeded]} adjustsFontSizeToFit numberOfLines={1}>{value === null ? '–' : formatted}</Text>
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

function PinStat({ label, description, value, icon }: { label: string; description: string; value: string; icon: IconName }) {
  const local = useThemeStyles(createLocalStyles);
  const colors = useColors();

  return (
    <View style={local.pinStat} accessible accessibilityLabel={`${description}, ${value}`}>
      <View style={local.statHeading}>
        <Icon name={icon} size={13} color={icon === 'delta' ? colors.tertiary : colors.secondary} />
        <Text style={local.statLabel}>{label}</Text>
      </View>
      <Text style={local.statValue} adjustsFontSizeToFit numberOfLines={1}>{value}</Text>
    </View>
  );
}

export function Gpu({ frame, live, connectionCard }: TelemetryProps) {
  const styles = useStyles();
  const local = useThemeStyles(createLocalStyles);
  const { number, formatReading } = useContext(ReadingFormatContext);
  const [source, setSource] = useState('');

  if (!frame) return <Page title="GPU">{connectionCard}</Page>;

  const groups = gpuDeviceGroups(frame.sensors);
  const selected = groups.find(group => group.key === source) ?? groups.find(group => group.source === NvidiaId) ?? groups[0];
  const readings = selected?.readings ?? [];
  const connectorPower = selected && gpuConnectorPower(frame.sensors, frame.connectorPowerKey, readings[0].identity.deviceId, groups.length === 1);
  const gpuPower = readings.filter(isGpuPowerReading);
  const health = gpuStatusRows(readings, formatReading);
  const performance = readings.filter(reading => reading.identity.component === 'GpuPerformance' && !['SupportedClockReasons', 'ClockLimitDuration'].includes(reading.identity.metric));
  const boardPower = gpuPower.find(r => r.identity.component === 'GpuBoard' && r.identity.metric === 'Power');
  const enforced = findMetric(gpuPower, 'GpuPowerLimits', 'EnforcedPowerLimit');
  const limits = boostLimitRows(readings);
  const pcie = readings.filter(reading => reading.identity.component === 'GpuPcie');

  const link = linkSpeed(pcie, 'LinkGeneration', 'LinkWidth', number);
  const maximumLink = linkSpeed(pcie, 'MaximumSystemLinkGeneration', 'MaximumLinkWidth', number);
  const traffic = pcie.filter(r => ['ReceiveThroughput', 'TransmitThroughput'].includes(r.identity.metric));

  return (
    <Page title="GPU" description={lastReading(frame, live)}>
      {connectionCard}
      {groups.length > 1 && <Selection selected={selected.key}
        options={groups.map(group => ({ id: group.key, label: group.name }))} onSelect={setSource} />}
      {selected ? (
        <>
          <GpuOverview rows={health} readings={readings} connectorPower={connectorPower} />
          {(gpuPower.length > 0 || performance.length > 0) && <Card title="GPU power status" icon="power">
            {boardPower && <BoardPower watts={boardPower} limit={enforced} />}
            <MetricGrid rows={[...gpuPower.filter(r => r !== boardPower), ...performance].map(reading => ({ reading }))} />
          </Card>}
          {limits.length > 0 && <Card title="GPU boost limits" icon="activity">
            <PanelList>
              <View style={local.limitRow}>
                <Text style={[styles.caption, local.limitName]}>Reason</Text>
                <Text style={[styles.caption, local.limitStatus]}>Status</Text>
                <Text style={[styles.caption, local.limitTime]}>Time limited</Text>
              </View>
              {limits.map(limit => <View key={limit.name} style={local.limitRow}>
                <Text style={[styles.text, local.limitName]}>{limit.name}</Text>
                <Text style={[styles.caption, limit.active === false && local.good, limit.active && local.warningStatus, local.limitStatus]}>
                  {limit.active === null ? 'N/A' : limit.active ? 'Active' : 'Idle'}
                </Text>
                <Text style={[styles.caption, local.limitTime]}>{limit.seconds === null ? 'N/A' : `${number(limit.seconds, 3)} s`}</Text>
              </View>)}
            </PanelList>
          </Card>}
          {pcie.length > 0 && <Card title="GPU PCIe status" icon="pcie">
            <View style={local.linkSummary}>
              <Text style={styles.caption}>Current link</Text>
              <View style={local.linkLine}>
                <Text style={local.linkValue}>{link.name}</Text>
                {link.rate && <Text style={styles.caption}>{link.rate}</Text>}
              </View>
              <LinkLanes readings={pcie} />
              <Text style={styles.caption}>Maximum link · {[maximumLink.name, maximumLink.rate].filter(Boolean).join(' · ')}</Text>
            </View>
            {traffic.length > 0 && <MetricGrid rows={traffic.map(reading => ({ reading }))} />}
            <PanelList>
              {pcie.filter(reading => !traffic.includes(reading) && !['LinkGeneration', 'LinkWidth', 'LinkSpeed', 'MaximumSystemLinkGeneration', 'MaximumLinkWidth',
                'MaximumLinkSpeed'].includes(reading.identity.metric)).map(reading => <Reading key={reading.identity.key} reading={reading}
                  value={reading.identity.metric === 'MaximumGpuLinkGeneration' && reading.quality === 'Valid' && reading.value !== null
                    ? `Gen ${reading.value}` : undefined} />)}
            </PanelList>
          </Card>}
        </>
      ) : <Empty icon="gpu" title="No GPU readings" live={live} />}
    </Page>
  );
}

function linkSpeed(readings: SensorReading[], genMetric: string, widthMetric: string, number: (value: number, digits: number) => string) {
  const generation = findMetric(readings, 'GpuPcie', genMetric)?.value;
  const width = findMetric(readings, 'GpuPcie', widthMetric)?.value;
  const rate = generation ? [0, 2.5, 5, 8, 16, 32, 64, 128][generation] : undefined;

  return {
    name: [generation && `Gen ${generation}`, width && `×${width}`].filter(Boolean).join(' ') || 'Unavailable',
    rate: rate ? `${number(rate, 1)} GT/s per lane` : undefined,
  };
}

export function Warnings({ frame, live, connectionCard, simulations, history, showHistory, warningActions }: TelemetryProps & {
  simulations: SensorFrame['simulationWarnings']; history: ReactNode; showHistory?: boolean; warningActions?: WarningActions;
}) {
  const styles = useStyles();
  const [view, setView] = useState<'active' | 'history'>(showHistory ? 'history' : 'active');
  const warnings = [...frame?.warnings ?? []].sort((a, b) => (b.startedAt ?? '').localeCompare(a.startedAt ?? ''));

  return (
    <Page title="Warnings" description={lastReading(frame, live)}>
      <Selection options={[{ id: 'active', label: 'Active' }, { id: 'history', label: 'History' }]} selected={view} onSelect={setView} />
      {connectionCard}
      {view === 'history' ? (live ? history : null) : frame && <>
        {warnings.length === 0 && (
          <Card title={live ? 'No active warnings' : 'No warnings in the last snapshot'} icon={live ? 'check' : 'warnings'}>{null}</Card>
        )}
        {warnings.map(warning => <WarningCard key={warning.episodeId} title={warning.title} detail={warning.detail}
          metadata={[sourceName(warning.pluginId), warning.startedAt && time(warning.startedAt)].filter(Boolean).join(' · ')}
          status={warning.phase}>
          {live && warningActions && frame.warningInstance && warning.phase === 'Active' && !warning.isSimulation &&
            <ActiveWarningActions warning={warning} instanceId={frame.warningInstance} actions={warningActions} />}
        </WarningCard>)}
        {simulations.length > 0 && <Text accessibilityRole="header" style={styles.heading}>Simulation</Text>}
        {simulations.map(warning => <WarningCard key={warning.id} title={warning.title} detail={warning.detail}
          metadata={time(warning.startedAt)} status="Simulation" />)}
      </>}
    </Page>
  );
}

function WarningCard({ title, detail, metadata, status, children }: {
  title: string; detail: string; metadata: string; status: string; children?: ReactNode;
}) {
  const styles = useStyles();
  const colors = useColors();
  const local = useThemeStyles(createLocalStyles);

  return (
    <Card title={title} icon="warnings">
      <View style={styles.row}>
        <Icon name="warnings" color={colors.warning} />
        <Text style={local.warningStatus}>{status}</Text>
      </View>
      {detail && <Text style={styles.text}>{detail}</Text>}
      <Text style={styles.caption}>{metadata}</Text>
      {children}
    </Card>
  );
}

function Empty({ icon, title, live }: { icon: IconName; title: string; live: boolean }) {
  const styles = useStyles();

  return <Card title={title} icon={icon}>
    <Text style={styles.caption}>{live ? 'The connected service has no readings for this view yet.' : 'Connect to your PC to see live readings.'}</Text>
  </Card>;
}

function isFaultStatus(reading: SensorReading) {
  return reading.identity.metric === 'FaultStatus' || reading.identity.metric === 'FaultLog';
}

function Reading({ reading, label, value }: { reading: SensorReading; label?: string; value?: string }) {
  const { formatReading } = useContext(ReadingFormatContext);
  const fault = isFaultStatus(reading) && reading.quality === 'Valid' && reading.value !== null && reading.value !== 0;

  return <ReadingRow label={label ?? metricLabel(reading)} value={value ?? formatReading(reading)} icon={readingIcon(reading)} fault={fault} />;
}

const metricIcons: Partial<Record<string, IconName>> = {
  Temperature: 'temp', Clock: 'clock', Utilization: 'usage', MemoryUsed: 'memory', MemoryFree: 'memory', MemoryTotal: 'memory',
  Voltage: 'voltage', Current: 'current', Power: 'power', PowerCapability: 'shield', FanSpeed: 'fan', FanDuty: 'fan', FanAutomatic: 'fan',
  FaultStatus: 'warnings', FaultLog: 'warnings', PerformanceState: 'gpu', SupportedPerformanceStates: 'gpu',
  ReceiveThroughput: 'receive', TransmitThroughput: 'transmit', ReplayCount: 'refresh', ReplayRolloverCount: 'refresh',
};

export function readingIcon({ identity }: SensorReading): IconName {
  if (identity.component === 'GpuMemory' && identity.metric === 'Clock') return 'memory';
  if (identity.component === 'GpuPowerLimits') return 'power';
  if (Object.hasOwn(metricIcons, identity.metric)) return metricIcons[identity.metric] ?? 'activity';

  return identity.component === 'GpuPcie' ? 'pcie' : 'activity';
}

export function ReadingRow({ label, value, icon, fault = false }: { label: string; value: string; icon?: IconName; fault?: boolean }) {
  const styles = useStyles();
  const local = useThemeStyles(createLocalStyles);
  const colors = useColors();
  
  return (
    <View style={local.reading}>
      {icon && <View style={local.readingIcon}>
        <Icon name={icon} color={fault ? colors.danger : colors.secondary} size={17} />
      </View>}
      <Text style={[styles.text, local.readingLabel]}>{label}</Text>
      {fault ? <View style={local.faultBadge}><View style={local.faultDot} /><Text style={local.faultText}>{value}</Text></View>
        : <Text style={[local.readingValue, value === 'OK' && local.good, value === 'Unavailable' && local.unavailable]}>{value}</Text>}
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
  statusDot: { width: 8, height: 8, borderRadius: 4 },
  status: { fontSize: 14, fontWeight: '700' },
  linkSummary: { gap: 8 },
  linkLine: { flexDirection: 'row', alignItems: 'baseline', flexWrap: 'wrap', justifyContent: 'space-between', gap: 6 },
  linkValue: { color: colors.tertiary, fontSize: 25, fontWeight: '700', fontVariant: ['tabular-nums'] },
  tileValue: { color: colors.secondary, fontWeight: '700', fontSize: 22, fontVariant: ['tabular-nums'] },
  unavailable: { color: colors.muted, fontSize: 13 },
  section: { gap: 12 },
  reading: { flexDirection: 'row', alignItems: 'center', gap: 9, minHeight: 43, paddingVertical: 7, paddingHorizontal: 2 },
  readingIcon: { width: 26, height: 26, borderRadius: 7, alignItems: 'center', justifyContent: 'center', backgroundColor: colors.raised },
  readingLabel: { flex: 1 },
  readingValue: { color: colors.text, flexShrink: 1, maxWidth: '55%', textAlign: 'right', fontWeight: '700', fontVariant: ['tabular-nums'] },
  faultBadge: { flexDirection: 'row', alignItems: 'center', gap: 6, backgroundColor: colors.dangerSurface,
    borderRadius: 12, paddingHorizontal: 9, paddingVertical: 4, maxWidth: '55%' },
  faultDot: { width: 6, height: 6, borderRadius: 3, backgroundColor: colors.danger },
  faultText: { color: colors.danger, fontSize: 13, fontWeight: '600', flexShrink: 1 },
  good: { color: colors.success },
  pinSummary: { flexDirection: 'row', gap: 7 },
  pinStat: { flex: 1, minWidth: 0, borderRadius: 10, borderWidth: 1, borderColor: colors.line,
    paddingHorizontal: 7, paddingVertical: 7, backgroundColor: colors.raised, gap: 4 },
  statHeading: { flexDirection: 'row', gap: 4, alignItems: 'center' },
  statLabel: { color: colors.muted, fontSize: 11, fontWeight: '600', flexShrink: 1 },
  statValue: { color: colors.text, fontSize: 13, fontWeight: '700', fontVariant: ['tabular-nums'] },
  pinLimit: { flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center', gap: 6, alignSelf: 'flex-start', paddingHorizontal: 8,
    paddingVertical: 2, borderWidth: 1, borderColor: colors.line, borderRadius: 8, backgroundColor: colors.raised },
  chart: { flexDirection: 'row', gap: 6, marginTop: 5 },
  pin: { flex: 1, alignItems: 'center', gap: 6 },
  pinValue: { color: colors.text, fontSize: 12, fontWeight: '700', width: '100%', textAlign: 'center', fontVariant: ['tabular-nums'] },
  track: { backgroundColor: colors.raised, width: '66%', height: 104, borderRadius: 6, overflow: 'hidden', justifyContent: 'flex-end' },
  fill: { backgroundColor: colors.secondary, borderRadius: 4 },
  voltageFill: { backgroundColor: colors.tertiary },
  exceeded: { color: colors.danger },
  overLimit: { backgroundColor: colors.danger },
  warningStatus: { color: colors.warning, fontSize: 13, fontWeight: '700' },
  limitRow: { flexDirection: 'row', alignItems: 'center', gap: 6, paddingVertical: 8 },
  limitName: { flex: 1 },
  limitStatus: { width: 58, textAlign: 'right' },
  limitTime: { width: 84, textAlign: 'right', fontVariant: ['tabular-nums'] },
});
