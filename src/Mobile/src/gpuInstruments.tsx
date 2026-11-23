import { useContext } from 'react';
import { StyleSheet, Text, View } from 'react-native';
import Svg, { Circle } from 'react-native-svg';
import { type Colors, useColors, useThemeStyles } from './appearance';
import { findMetric, metricLabel, type gpuStatusRows } from './metrics';
import { ReadingFormatContext } from './mobileSettings';
import type { SensorReading } from './protocol';
import { Card, useStyles } from './ui';

type Row = ReturnType<typeof gpuStatusRows>[number];

function limitRatio(ratio: number) { return Math.max(0, Math.min(1, ratio)); }

function Meter({ ratio }: { ratio: number }) {
  const local = useThemeStyles(styles);
  return <View style={local.track}><View style={[local.fill, { width: `${100 * limitRatio(ratio)}%` }]} /></View>;
}

function ValueWithUnits({ value }: { value: string }) {
  const local = useThemeStyles(styles);
  return <>{value.split(/(MHz|GiB|MB|mV|°C|°F|MiB\/s|KiB\/s|B\/s|RPM|GT\/s|W|V|A|%)(?=\s|$)/g).map((part, i) =>
    i % 2 ? <Text key={i} style={local.unit}>{part}</Text> : part)}</>;
}

function Metric({ row, inGrid = false }: { row: Row; inGrid?: boolean }) {
  const local = useThemeStyles(styles);
  const { formatReading } = useContext(ReadingFormatContext);
  const name = row.label ?? metricLabel(row.reading);
  const value = row.value ?? formatReading(row.reading);
  const internalClock = row.reading.identity.component === 'GpuCrossbar' || row.reading.identity.component === 'GpuSystem';
  return <View style={[local.cell, inGrid && local.gridCell]} accessible accessibilityLabel={`${name}, ${value}`}>
    <Text style={local.label}>{name}</Text>
    <Text style={[local.value, internalClock && local.internalClock, value === 'Unavailable' && local.muted]}>
      <ValueWithUnits value={value} />
    </Text>
  </View>;
}

export function MetricGrid({ rows }: { rows: Row[] }) {
  const local = useThemeStyles(styles);
  return <View style={local.grid}>{rows.map(row => <Metric key={row.reading.identity.key} row={row} inGrid />)}</View>;
}

const connectorMetrics = [
  { metric: 'Power', label: 'Power' }, { metric: 'Current', label: 'Amperage' }, { metric: 'Voltage', label: 'Voltage' },
];

export function ConnectorSummary({ readings }: { readings: SensorReading[] }) {
  const local = useThemeStyles(styles);
  const { formatReading } = useContext(ReadingFormatContext);
  const other = readings.filter(r => !connectorMetrics.some(c => c.metric === r.identity.metric));
  return <>
    {other.length < readings.length && <View style={local.connectorRow}>
      {connectorMetrics.map(({ metric, label }) => {
        const reading = readings.find(r => r.identity.metric === metric);
        const value = formatReading(reading);
        return <View key={metric} style={local.connectorCell} accessible accessibilityLabel={`${label}, ${value}`}>
          <Text style={local.label}>{label}</Text>
          <Text adjustsFontSizeToFit minimumFontScale={0.7} numberOfLines={1} style={[local.value, value === 'Unavailable' && local.muted]}>
            <ValueWithUnits value={value} />
          </Text>
        </View>;
      })}
    </View>}
    {other.map(reading => <View key={reading.identity.key} style={local.connectorDetail}>
      <Text style={local.label}>{metricLabel(reading)}</Text>
      <Text style={local.connectorDetailValue}>{formatReading(reading)}</Text>
    </View>)}
  </>;
}

export function GpuOverview({ rows, readings }: { rows: Row[]; readings: SensorReading[] }) {
  const local = useThemeStyles(styles);
  const colors = useColors();
  const { formatReading } = useContext(ReadingFormatContext);
  const usage = rows.find(({ reading: r }) => r.identity.component === 'GpuCore' && r.identity.metric === 'Utilization');
  const voltage = rows.find(({ reading: r }) => r.identity.component === 'GpuCore' && r.identity.metric === 'Voltage');
  const memory = rows.find(({ reading: r }) => r.identity.metric === 'MemoryUsed');
  const total = findMetric(readings, 'GpuMemory', 'MemoryTotal')?.value;
  const used = memory?.reading.quality === 'Valid' ? memory.reading.value : null;
  const percent = usage?.reading.quality === 'Valid' ? usage.reading.value : null;
  const clocks = rows.filter(({ reading }) => reading.identity.metric === 'Clock');
  const temps = rows.filter(({ reading }) => reading.identity.metric === 'Temperature');
  const fans = rows.filter(({ reading }) => reading.identity.component === 'GpuFan');
  const other = rows.filter(row => row !== usage && row !== voltage && row !== memory &&
    !clocks.includes(row) && !temps.includes(row) && !fans.includes(row));
  return <>
    {(usage || voltage || memory) && <Card title="GPU status" icon="gpu"><View style={local.hero}>
      {usage && <View style={local.gauge} accessible accessibilityLabel={`GPU usage, ${formatReading(usage.reading)}`}>
        <Text style={local.label}>GPU usage</Text>
        <View style={local.dial}>
          <Svg width={92} height={92} viewBox="0 0 100 100">
            <Circle cx={50} cy={50} r={45} fill="none" stroke={colors.line} strokeWidth={6} />
            {percent != null && percent > 0 && <Circle cx={50} cy={50} r={45} fill="none" stroke={colors.accent}
              strokeWidth={6} strokeDasharray={[Math.PI * 90 * limitRatio(percent / 100), Math.PI * 90]}
              rotation={-90} origin="50,50" />}
          </Svg>
          <View style={local.dialText}><Text style={local.dialValue} adjustsFontSizeToFit numberOfLines={1}>{formatReading(usage.reading)}</Text></View>
        </View>
      </View>}
      {(voltage || memory) && <View style={local.summary}>
        {voltage && <Metric row={voltage} />}
        {memory && <Metric row={memory} />}
        {used != null && total != null && total > 0 && <Meter ratio={used / total} />}
      </View>}
    </View></Card>}
    {clocks.length > 0 && <Card title="Clocks" icon="clock"><MetricGrid rows={clocks} /></Card>}
    {temps.length > 0 && <Card title="Temperatures" icon="temp"><MetricGrid rows={temps} /></Card>}
    {fans.length > 0 && <Card title="Fans" icon="fan">{fans.map(row => <View key={row.reading.identity.key} style={local.fan}>
      <Text style={local.label}>{row.label ?? metricLabel(row.reading)}</Text>
      <Text style={local.fanValue}>{row.value ?? formatReading(row.reading)}</Text>
    </View>)}</Card>}
    {other.length > 0 && <Card title="Other GPU readings" icon="activity"><MetricGrid rows={other} /></Card>}
  </>;
}

export function BoardPower({ watts, limit }: { watts: SensorReading; limit?: SensorReading }) {
  const { number, formatReading } = useContext(ReadingFormatContext);
  const shared = useStyles();
  const local = useThemeStyles(styles);
  const ratio = watts.quality === 'Valid' && watts.value != null && limit?.value != null && limit.value > 0
    ? watts.value / limit.value : null;
  return <View style={local.power}>
    <Text style={local.label}>Board power</Text><Text style={local.headline}>{formatReading(watts)}</Text>
    {ratio !== null && <><Meter ratio={ratio} />
      <Text style={shared.caption}>{number(ratio * 100, 0)}% of {formatReading(limit)} enforced limit</Text></>}
  </View>;
}

export function LinkLanes({ readings }: { readings: SensorReading[] }) {
  const local = useThemeStyles(styles);
  const current = findMetric(readings, 'GpuPcie', 'LinkWidth')?.value;
  const max = findMetric(readings, 'GpuPcie', 'MaximumLinkWidth')?.value;
  if (current == null || max == null || !Number.isInteger(max) || !Number.isInteger(current) ||
    max < 1 || max > 32 || current < 0 || current > max) return null;
  return <View style={local.lanes} accessible accessibilityLabel={`${current} of ${max} PCIe lanes active`}>
    {Array.from({ length: max }, (_, i) => <View key={i} style={[local.lane, i < current && local.activeLane]} />)}
  </View>;
}

const styles = (colors: Colors) => StyleSheet.create({
  grid: { flexDirection: 'row', flexWrap: 'wrap', gap: 6 },
  connectorDetail: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', gap: 8 },
  connectorDetailValue: { color: colors.text, fontWeight: '600', fontVariant: ['tabular-nums'] },
  connectorRow: { flexDirection: 'row', gap: 6 },
  connectorCell: { flex: 1, minWidth: 0, paddingHorizontal: 7, paddingVertical: 10, gap: 4, borderRadius: 8, backgroundColor: colors.raised },
  gridCell: { flexBasis: '45%', flexGrow: 1 },
  cell: { padding: 10, gap: 3, backgroundColor: colors.raised, borderRadius: 8 },
  value: { color: colors.secondary, fontSize: 20, fontWeight: '700', fontVariant: ['tabular-nums'] },
  unit: { color: colors.muted, fontSize: 14 }, internalClock: { color: colors.tertiary },
  label: { color: colors.muted, fontSize: 13 }, muted: { color: colors.muted, fontSize: 15 },
  hero: { flexDirection: 'row', gap: 12, alignItems: 'center' }, summary: { flex: 1, gap: 6 },
  gauge: { alignItems: 'center', gap: 8 }, dial: { width: 92, height: 92 },
  dialText: { ...StyleSheet.absoluteFill, justifyContent: 'center', alignItems: 'center', paddingHorizontal: 8 },
  dialValue: { color: colors.accent, fontWeight: '700', fontSize: 24 },
  fan: { flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center', gap: 10 },
  fanValue: { color: colors.secondary, flexShrink: 1, fontWeight: '600', fontVariant: ['tabular-nums'] },
  power: { gap: 5 }, headline: { fontSize: 31, fontWeight: '700', color: colors.accent },
  track: { height: 6, backgroundColor: colors.line, borderRadius: 3, overflow: 'hidden' },
  fill: { height: '100%', backgroundColor: colors.accent },
  lanes: { flexDirection: 'row', gap: 2 }, lane: { flex: 1, height: 9, backgroundColor: colors.line, borderRadius: 2 },
  activeLane: { backgroundColor: colors.tertiary },
});
