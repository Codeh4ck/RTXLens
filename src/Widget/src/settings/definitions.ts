import type { SettingDefinition } from './SettingDefinition.ts';
import type { SettingOption } from './SettingOption.ts';
import { warningOptions } from '../warnings/previewWarnings.ts';

const options = (...values: string[]): SettingOption[] => values.map((value) => ({ key: value, label: value }));

const panelOptions: SettingOption[] = [
    ['autoPins', 'Automatic pin source'],
    ['autoStatus', 'Automatic status source'],
    ['asusPins', 'ROG Astral pins'],
    ['nvidiaStatus', 'GPU status (GenericNvidia)'],
    ['wireviewPins', 'WireView Pro II pins'],
    ['wireviewStatus', 'WireView Pro II temperatures and faults'],
    ['msiConnector1', 'MSI PSU connector 1'],
    ['msiConnector2', 'MSI PSU connector 2'],
    ['msiStatus', 'MSI PSU status'],
    ['off', 'Off']
].map(([key, label]) => ({ key: key!, label: label! }));

export const definitions: SettingDefinition[] = [];

function add(
    group: string,
    id: string,
    label: string,
    type: SettingDefinition['type'],
    defaultValue: SettingDefinition['defaultValue'],
    extra: Partial<SettingDefinition> = {}
): void {
    definitions.push({
        group,
        id,
        label,
        type,
        defaultValue,
        ...extra
    });
}

function slider(group: string, id: string, label: string, value: number, min: number, max: number, unit = '%'): void {
    add(group, id, label, 'slider', value, {
        min,
        max,
        step: 1,
        unit
    });
}

const layout = 'Panels and telemetry';
add(layout, 'precision', 'Decimal places', 'combobox', '1', { options: options('0', '1', '2') });
add(layout, 'panelCount', 'Panel layout within the allocated iCUE slot', 'combobox', 'Auto', {
    options: options('Auto', 'One', 'Two', 'Three')
});
add(layout, 'smallPanel', 'First panel when only one fits', 'combobox', '1', {
    options: options('1', '2', '3').map((option) => ({ ...option, label: `Panel ${option.key}` }))
});
add(layout, 'autoPage', 'Automatically page compact slots', 'switch', false);
slider(layout, 'pageSeconds', 'Compact page interval', 10, 3, 60, 's');
add(layout, 'showIcons', 'Show SVG icons', 'switch', true);
add(layout, 'showSource', 'Show panel source labels', 'switch', true);
add(layout, 'showAlerts', 'Show service alerts (banner and warning flyouts)', 'switch', true);
add(layout, 'alertPulse', 'Pulse active service alerts', 'switch', false);
add(layout, 'warningPreview', 'Warning flyout preview (simulated, display only)', 'combobox', 'none', { options: warningOptions });

for (let panel = 1; panel <= 3; panel++) {
    const group = layout;
    add(
        group,
        `panel${panel}`,
        `PANEL ${panel} - Content`,
        'combobox',
        panel === 1 ? 'autoPins' : panel === 2 ? 'autoStatus' : 'msiConnector1',
        { options: panelOptions }
    );
    add(group, `panel${panel}Title`, 'Custom panel title (optional)', 'textfield', '');
    add(group, `panel${panel}Hero`, 'Show summary metrics', 'switch', true);
}

add(layout, 'checkUpdates', 'Automatically check for updates (startup and scheduled)', 'switch', true);
add(layout, 'updateFrequency', 'Update check frequency', 'combobox', 'Daily', { options: options('Daily', 'Weekly', 'Monthly') });

const sources = layout;

for (const prefix of ['asus', 'wireview']) {
    const label = prefix === 'asus' ? 'Astral' : 'WireView Pro II';
    add(sources, `${prefix}Metric`, `${label} pin metric`, 'combobox', 'A', { options: options('A', 'W', 'V') });
    add(sources, `${prefix}Compare`, `${label}: show PSU current beneath pins`, 'switch', false);
    add(sources, `${prefix}Connector`, `${label}: PSU comparison connector`, 'combobox', '1', { options: options('1', '2') });
}

const gpuStatus = 'GPU Status settings';
add(gpuStatus, 'showFanDuty', 'Show reported fan duty after RPM', 'switch', true);

const charts = layout;
add(gpuStatus, 'temperatureUnit', 'Temperature unit', 'combobox', 'iCUE', { options: options('iCUE', '°C', '°F') });
add(charts, 'numberLocale', 'Number locale (blank follows system)', 'textfield', '');
add(charts, 'showUnits', 'Show units', 'switch', true);
add(charts, 'showAxis', 'Show left axis', 'switch', true);
add(charts, 'showGrid', 'Show chart grid', 'switch', true);
add(charts, 'showHeadroom', 'Show current headroom to actual protection limit', 'switch', true);
add(charts, 'showSpread', 'Show current spread and highest/lowest pin', 'switch', true);
add(charts, 'showPeak', 'Show local peak hold', 'switch', false);
slider(charts, 'currentMax', 'Current display axis maximum (not a protection limit)', 10, 6, 20, 'A');
slider(charts, 'powerCableFallback', 'Power display range when cable capability is unknown', 600, 150, 1200, 'W');
slider(charts, 'barWidth', 'Bar width', 70, 25, 95);
slider(charts, 'barGap', 'Pin spacing', 50, 25, 160);
slider(charts, 'barRadius', 'Bar corner radius (% of bar width)', 15, 0, 50);
const appearance = 'Widget Personalization';
add(appearance, 'textColor', 'Text Color', 'color', '#e7f3f4');
add(appearance, 'accentColor', 'Accent Color', 'color', '#27d8bd');
add(appearance, 'backgroundColor', 'Background Color', 'color', '#080f16');
add(appearance, 'backgroundMedia', 'Background Image', 'media-selector', null);
slider(appearance, 'bgBrightness', 'Background Brightness', 65, 0, 150);
slider(appearance, 'glassBlur', 'Glass Blur', 0, 0, 24, 'px');
slider(appearance, 'transparency', 'Background Transparency', 0, 0, 100);
add(appearance, 'theme', 'Theme', 'combobox', 'Dark', { options: options('Dark', 'Light', 'Custom') });
add(appearance, 'reduceMotion', 'Disable alert animation', 'switch', false);
slider(appearance, 'panelOpacity', 'Panel opacity', 96, 20, 100);
slider(appearance, 'panelPadding', 'Panel padding', 100, 60, 140);
slider(appearance, 'panelGap', 'Panel spacing', 100, 40, 160);
slider(appearance, 'panelRadius', 'Panel corner radius', 10, 0, 24, 'px');
slider(appearance, 'borderWidth', 'Panel border width', 1, 0, 3, 'px');
slider(appearance, 'glow', 'Neon glow', 12, 0, 30, 'px');
add(appearance, 'showScanlines', 'Show HUD scan lines', 'switch', false);
slider(appearance, 'scanlines', 'HUD scanline opacity', 8, 0, 25);
slider(appearance, 'iconSize', 'Icon size', 140, 65, 140);
const colors: Record<string, [string, string]> = {
    mutedColor: ['Secondary text', '#a5bdcb'],
    unitColor: ['Units', '#a5bdcb'],
    panelColor: ['Panels', '#101e2b'],
    borderColor: ['Borders', '#294754'],
    trackColor: ['Bar tracks', '#223644'],
    gridColor: ['Chart grid', '#294754'],
    axisColor: ['Axis labels', '#a5bdcb'],
    peakColor: ['Peak hold', '#b894ff'],
    spreadColor: ['Current spread', '#22edff'],
    headroomColor: ['Headroom', '#6bffbb'],
    comparisonColor: ['PSU comparison', '#00ffce'],
    connectedColor: ['Connected', '#60e5a8'],
    warningColor: ['Warning and stale', '#ffbd69'],
    errorColor: ['Fault and error', '#ff6179'],
    alertColor: ['Alert background', '#341b26']
};

for (const [id, [label, value]] of Object.entries(colors)) {
    add(appearance, id, `${label} color (Custom theme)`, 'color', value);
}

add(appearance, 'pinFillColor', 'Pin fill colour (all pins)', 'color', '#27d8bd');
add(appearance, 'fontFamily', 'Font family (entire widget)', 'textfield', 'Consolas');

for (const [role, label, size] of [
    ['title', 'Panel titles', 110],
    ['label', 'Metric labels', 130],
    ['value', 'Metric values', 120],
    ['unit', 'Units', 115],
    ['pin', 'Pin labels and values', 130],
    ['axis', 'Axis text', 140],
    ['comparison', 'Headroom and PSU comparison', 140],
    ['alert', 'Alerts', 100]
] as const) {
    slider(appearance, `${role}Size`, `${label}: size`, size, 70, 140);
}

export const defaults = Object.fromEntries(definitions.map((definition) => [definition.id, definition.defaultValue]));