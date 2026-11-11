import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { parseSnapshot } from '../src/telemetry/parseSnapshot.ts';
import { TelemetryStore } from '../src/telemetry/TelemetryStore.ts';
import { sensorKey } from '../src/telemetry/sensorKey.ts';
import { Settings } from '../src/settings/Settings.ts';
import { Formatter } from '../src/formatting/Formatter.ts';
import { PeakStore } from '../src/telemetry/PeakStore.ts';
import { PanelPresenter } from '../src/panels/PanelPresenter.ts';
import { resolvePanel } from '../src/panels/resolvePanel.ts';
import { ReadOnlyApi } from '../src/transport/ReadOnlyApi.ts';
import { normalizeMediaConfig } from '../src/media/normalizeMediaConfig.ts';
import { definitions } from '../src/settings/definitions.ts';
import { fixture, configuration, catalog } from './fixtures.mjs';

function context(overrides = {}, snapshot = fixture()) {
    const settings = new Settings();
    const configured = { numberLocale: 'en-GB', ...overrides };
    settings.update(name => configured[name]);
    const store = new TelemetryStore();
    store.update(parseSnapshot(snapshot));
    store.configuration = configuration();
    store.catalog = catalog().plugins;
    store.connection = 'live';
    const format = new Formatter(settings);
    format.configure('°C');
    const presenter = new PanelPresenter(store, settings, format, new PeakStore());
    return { settings, store, presenter, format, now: Date.parse(snapshot.timestamp) };
}

test('schema one accepts the backend enum names and creates distinct producer identities', () => {
    const snapshot = parseSnapshot(fixture());
    const powers = snapshot.sensors.filter(reading => reading.identity.component === 'GpuConnector' && reading.identity.metric === 'Power');
    assert.equal(new Set(powers.map(reading => reading.identity.key)).size, 2);
    assert.match(powers[0].identity.key, /gpuConnector\/default\/1\/power$/);
});

for (const [name, mutate] of [
    ['unknown schema', frame => frame.schemaVersion = 2],
    ['non-finite value', frame => frame.sensors[0].value = NaN],
    ['metric/unit mismatch', frame => frame.sensors[0].unit = 'Amperes'],
    ['invalid date', frame => frame.sensors[0].observedAt = 'yesterday'],
    ['duplicate identity', frame => frame.sensors.push(structuredClone(frame.sensors[0]))],
    ['invalid alert', frame => frame.alerts.push({})],
    ['null warning state', frame => frame.warnings.states.push(null)],
    ['invalid warning sequence', frame => frame.warnings.sequence = -1]
]) test(`rejects ${name}`, () => { const frame = fixture(); mutate(frame); assert.throws(() => parseSnapshot(frame)); });

test('identity escaping matches RFC3986 and retains connector scope', () => {
    const identity = { pluginId: "producer'", deviceId: 'a/b', component: 'PsuPin', metric: 'Current', channel: 1, connectorId: '1' };
    assert.equal(sensorKey(identity), 'producer%27/a%2Fb/psuPin/1/1/current');
    assert.notEqual(sensorKey(identity), sensorKey({ ...identity, connectorId: '2' }));
});

test('valid zero is preserved and stale readings become unavailable without a new frame', () => {
    const { store, now } = context();
    const device = store.device('msi-psu', '', now);
    assert.equal(store.value('msi-psu', device, 'PsuPin', 'Current', now, 1, '2'), 0);
    assert.equal(store.value('msi-psu', device, 'PsuPin', 'Current', now + 5001, 1, '2'), null);
    assert.equal(store.state('msi-psu', now + 5001), 'Stale');
});

test('disabled or failed producers cannot retain apparently valid values', () => {
    const { store, now } = context();
    store.configuration.configuration.plugins['asus-astral'].enabled = false;
    assert.equal(store.reporting('asus-astral', now), false);
    store.snapshot.plugins.find(plugin => plugin.pluginId === 'msi-psu').state = 'Error';
    assert.equal(store.reporting('msi-psu', now), false);
});

test('panels always use the reporting device and ignore retired device settings', () => {
    const { settings, store, presenter, now } = context({ panel1: 'asusPins', asusDevice: 'not-present' });
    const binding = resolvePanel(1, settings, store, now);
    assert.equal(binding.deviceId, '/gpu-nvidia/0');
    assert.equal(store.value(binding.pluginId, binding.deviceId, 'GpuPin', 'Current', now, 1), 5.82);
    assert.equal(presenter.present(binding, now).state, 'Ready');
});

test('automatic pins prefer an available pin source while GPU status can remain on a non-Astral GPU', () => {
    const frame = fixture();
    frame.sensors = frame.sensors.filter(reading => reading.identity.pluginId !== 'asus-astral' || reading.identity.component !== 'GpuPin');
    const { settings, store, now, presenter } = context({}, frame);
    assert.equal(resolvePanel(1, settings, store, now).pluginId, 'wireview-pro2');
    assert.equal(resolvePanel(2, settings, store, now).pluginId, 'asus-astral');
    settings.update(name => ({ panel1: 'asusPins' })[name]);
    assert.equal(presenter.present(resolvePanel(1, settings, store, now), now).state, 'Pins unavailable');
});

test('GPU and WireView summaries move to status panels and remain scoped to their producer', () => {
    const frame = fixture();
    frame.sensors.find(reading => reading.identity.pluginId === 'wireview-pro2' && reading.identity.component === 'GpuConnector' && reading.identity.metric === 'Power').value = 500;
    const { settings, store, presenter, now } = context({ panel1: 'asusStatus', panel2: 'wireviewStatus', panel3: 'asusPins' }, frame);
    const asus = presenter.present(resolvePanel(1, settings, store, now), now);
    const wire = presenter.present(resolvePanel(2, settings, store, now), now);
    assert.deepEqual(asus.summary.map(metric => metric.id), ['power', 'current', 'voltage']);
    assert.deepEqual(wire.summary.map(metric => metric.id), ['power', 'current', 'voltage']);
    assert.equal(asus.summary[0].text, '432.0 W');
    assert.deepEqual(asus.summary.map(metric => metric.label), ['Power', 'Total current', 'Voltage']);
    assert.equal(wire.summary[0].text, '500.0 W');
    assert.equal(asus.gpuUsage, '97%');
    assert.equal(wire.gpuUsage, '');
    assert.equal(presenter.present(resolvePanel(3, settings, store, now), now).summary.length, 0);
    settings.update(name => ({ panel1: 'wireviewPins' })[name]);
    assert.equal(presenter.present(resolvePanel(1, settings, store, now), now).summary.length, 0);
    assert.ok(!asus.rows.some(metric => ['boardPower', 'gpuUsage'].includes(metric.id)));
});

test('MSI summaries retain their fixed defaults and ignore removed primary overrides', () => {
    const { settings, store, presenter, now } = context({ panel1: 'msiConnector1', panel1Primary: 'BoardPower', panel2: 'msiStatus' });
    assert.deepEqual(presenter.present(resolvePanel(1, settings, store, now), now).summary.map(metric => metric.id), ['connectorCurrent', 'temperature', 'fan']);
    assert.deepEqual(presenter.present(resolvePanel(2, settings, store, now), now).summary.map(metric => metric.id), ['temperature', 'fan']);
});

test('automatic source is labeled and fixed source stays fixed', () => {
    const { settings, store, presenter, now } = context();
    assert.match(presenter.present(resolvePanel(1, settings, store, now), now).source, /^Auto/);
    store.configuration.configuration.plugins['asus-astral'].enabled = false;
    assert.equal(resolvePanel(1, settings, store, now).pluginId, 'wireview-pro2');
    settings.update(name => ({ panel1: 'asusPins' })[name]);
    assert.equal(resolvePanel(1, settings, store, now).pluginId, 'asus-astral');
});

test('PSU comparisons choose each connector independently', () => {
    const { settings, store, presenter, now } = context({ panel1: 'asusPins', panel2: 'wireviewPins', asusCompare: true,
        wireviewCompare: true, asusConnector: '2', wireviewConnector: '1', numberLocale: 'en-GB' });
    const first = presenter.present(resolvePanel(1, settings, store, now), now);
    const second = presenter.present(resolvePanel(2, settings, store, now), now);
    assert.equal(first.pins[0].comparison, '0.0 A');
    assert.equal(second.pins[0].comparison, '5.8 A');
    assert.match(first.comparisonLabel, /C2/);
    assert.match(second.comparisonLabel, /C1/);
});

test('Astral reverses only the PSU comparison while WireView and MSI retain native pin order', () => {
    const frame = fixture();
    for (const reading of frame.sensors) {
        if (reading.identity.pluginId === 'msi-psu' && reading.identity.component === 'PsuPin') {
            reading.value = reading.identity.channel + (reading.identity.connectorId === '2' ? 10 : 0);
        }
    }
    const { settings, store, presenter, now } = context({ panel1: 'asusPins', panel2: 'wireviewPins', panel3: 'msiConnector2',
        asusCompare: true, wireviewCompare: true, asusConnector: '2', wireviewConnector: '1' }, frame);
    const asus = presenter.present(resolvePanel(1, settings, store, now), now);
    const wire = presenter.present(resolvePanel(2, settings, store, now), now);
    const msi = presenter.present(resolvePanel(3, settings, store, now), now);
    assert.deepEqual(asus.pins.map(pin => pin.comparison), ['16.0 A', '15.0 A', '14.0 A', '13.0 A', '12.0 A', '11.0 A']);
    assert.deepEqual(wire.pins.map(pin => pin.comparison), ['1.0 A', '2.0 A', '3.0 A', '4.0 A', '5.0 A', '6.0 A']);
    assert.deepEqual(msi.pins.map(pin => pin.number), [11, 12, 13, 14, 15, 16]);
    assert.deepEqual(asus.pins.map(pin => pin.number), [5.82, 6.18, 6.08, 5.92, 5.98, 6.02]);
});

test('PSU pins remain current-only and Ai1600TS is identified without invented watts', () => {
    const { settings, store, presenter, now } = context({ panel1: 'msiConnector2', asusMetric: 'W', numberLocale: 'en-GB' });
    const model = presenter.present(resolvePanel(1, settings, store, now), now);
    assert.match(model.source, /Ai1600TS/);
    assert.equal(model.axisUnit, 'A');
    assert.equal(model.pins[0].value, '0.0 A');
    assert.ok(model.pins.every(pin => pin.headroomValue === null));
    assert.ok(!model.summary.some(metric => /power|voltage/i.test(metric.label)));
});

test('voltage fills upward from 11.4 V and power uses per-pin cable share', () => {
    const { settings, store, presenter, now } = context({ panel1: 'wireviewPins', wireviewMetric: 'V' });
    const voltage = presenter.present(resolvePanel(1, settings, store, now), now);
    assert.ok(Math.abs(voltage.pins[0].fill - 50) < .001);
    settings.setPinMetric('wireview', 'W');
    const power = presenter.present(resolvePanel(1, settings, store, now), now);
    assert.equal(power.axisUnit, 'W');
    assert.ok(Math.abs(power.pins[0].fill - 69.84) < .001);
});

test('unknown headroom is never inferred from an axis; matching device limit is honored', () => {
    const { settings, store, presenter, now } = context({ panel1: 'wireviewPins', numberLocale: 'en-GB' });
    const binding = resolvePanel(1, settings, store, now);
    assert.equal(presenter.present(binding, now).pins[0].headroom, '-');
    store.wireLimit = 9.2;
    store.wireLimitDevice = 'com3';
    assert.equal(presenter.present(binding, now).pins[0].headroom, '+3.4 A');
    store.wireLimitDevice = 'different-device';
    assert.equal(presenter.present(binding, now).pins[0].headroom, '-');
});

test('settings fit two categories with contiguous numbered panel sections and no advanced text selectors', () => {
    assert.deepEqual([...new Set(definitions.map(definition => definition.group))], ['Panels and telemetry', 'GPU Status settings', 'Widget Personalization']);
    for (let panel = 1; panel <= 3; panel++) {
        const controls = definitions.filter(definition => new RegExp(`^panel${panel}(?:[A-Z]|$)`).test(definition.id));
        assert.deepEqual(controls.map(definition => definition.id), [`panel${panel}`, `panel${panel}Title`, `panel${panel}Hero`]);
        assert.equal(controls[0].label, `PANEL ${panel} - Content`);
        assert.ok(!controls[0].options.some(option => option.key === 'selection'));
    }
    const compact = definitions.find(definition => definition.id === 'smallPanel');
    assert.equal(compact.label, 'First panel when only one fits');
    assert.deepEqual(compact.options.map(option => option.label), ['Panel 1', 'Panel 2', 'Panel 3']);
});

test('widget defaults use one font, requested sizes and colours, and retire hardware/video/footer settings', () => {
    const settings = new Settings();
    settings.update(() => undefined);
    assert.equal(settings.text('fontFamily'), 'Consolas');
    assert.equal(settings.text('spreadColor'), '#22edff');
    assert.equal(settings.text('headroomColor'), '#6bffbb');
    assert.equal(settings.text('comparisonColor'), '#00ffce');
    for (const [id, expected] of Object.entries({ titleSize: 110, valueSize: 120, unitSize: 115,
        pinSize: 130, axisSize: 140, comparisonSize: 140, barRadius: 15, barGap: 50, iconSize: 140, labelSize: 130, precision: 1 })) assert.equal(settings.number(id), expected, id);
    assert.equal(definitions.filter(definition => /font/i.test(definition.id)).length, 1);
    assert.ok(!definitions.some(definition => /Weight$|Spacing$|Device$|Primary$|Accent$/.test(definition.id)));
    for (const id of ['showFooter', 'servicePort', 'visualStyle', 'mediaPlaying', 'mediaLoop', 'readyColor', 'coreWarningC', 'memoryWarningC']) {
        assert.ok(!definitions.some(definition => definition.id === id), id);
    }
    assert.equal(settings.enabled('showScanlines'), false);
    assert.ok(!definitions.some(definition => definition.id === 'heroSize'));
    assert.equal(definitions.filter(definition => /pin.*Color/.test(definition.id)).length, 1);
    assert.equal(settings.text('pinFillColor'), '#27d8bd');
});

test('GPU status orders clocks, VRAM usage, temperatures, and fans', () => {
    const { settings, store, presenter, now } = context({ panel1: 'asusStatus' });
    const rows = presenter.present(resolvePanel(1, settings, store, now), now).rows;
    assert.deepEqual(rows.map(row => row.id), ['coreClock', 'memoryClock', 'vram', 'temperatureCore', 'temperatureMemory', 'fan1', 'fan2']);
    assert.match(rows[2].text, /24.0 GB \/ 32.0 GB \(75%\)/);
});

test('temperature highlighting follows service warning phases instead of local thresholds', () => {
    for (const phase of ['Active', 'Recovering', 'Unavailable']) {
        const frame = fixture();
        frame.warnings.sequence = 1;
        frame.warnings.states = [{ id: 'core', pluginId: 'asus-astral', deviceId: '/gpu-nvidia/0', typeId: 'asus-astral/core-temperature', icon: 'temperature', phase, title: 'Core temperature', detail: '81 °C', episodeId: 'first', startedAt: frame.timestamp, lastNotificationAt: frame.timestamp, notificationSequence: 1, policy: { notifyOnStart: true, repeatWhileActive: false, repeatIntervalSeconds: 5, clearAfterSeconds: 2 } }];
        const { settings, store, presenter, now } = context({ panel1: 'asusStatus' }, frame);
        const rows = presenter.present(resolvePanel(1, settings, store, now), now).rows;
        assert.equal(rows.find(row => row.id === 'temperatureCore').warning, phase === 'Active');
        assert.equal(rows.find(row => row.id === 'temperatureMemory').warning, false);
    }
});

test('service alerts recover on the next snapshot and stale alerts do not remain active', () => {
    const frame = fixture();
    frame.alerts.push({ pluginId: 'asus-astral', deviceId: '/gpu-nvidia/0', kind: 'WireOvercurrent', reportedAt: frame.timestamp,
        pins: [{ channel: 2, currentA: 10 }], percentageDifference: null });
    const { settings, store, presenter, now } = context({ panel1: 'asusPins' }, frame);
    const binding = resolvePanel(1, settings, store, now);
    assert.equal(presenter.present(binding, now).state, 'Alert');
    assert.equal(presenter.present(binding, now + 5001).alerts.length, 0);
    store.snapshot.alerts = [];
    assert.equal(presenter.present(binding, now).state, 'Ready');
});

test('peak history is bounded, scoped by producer, and resettable', () => {
    const peaks = new PeakStore();
    assert.equal(peaks.observe('wireview/P1', 6), 6);
    assert.equal(peaks.observe('wireview/P1', 4), 6);
    assert.equal(peaks.observe('asus/P1', 4), 4);
    peaks.clear();
    assert.equal(peaks.observe('wireview/P1', 4), 4);
    assert.equal(peaks.observe('wireview/P1', null), null);
});

test('media config preserves both documented size aliases and rejects remote hosts', () => {
    const config = normalizeMediaConfig({ pathToAsset: 'file:///C:/media.png', baseSizeX: 100, baseSizeY: 200, scale: 1.5, positionX: 4, positionY: 5, angle: 6 });
    assert.deepEqual(config, { path: 'file:///C:/media.png', baseWidth: 100, baseHeight: 200, scale: 1.5, positionX: 4, positionY: 5, angle: 6 });
    assert.equal(normalizeMediaConfig('https://example.com/image.png'), null);
    assert.equal(normalizeMediaConfig('file:///C:/media.webm'), null);
    assert.equal(normalizeMediaConfig({ pathToAsset: 'file:///C:/media.mp4' }), null);
    assert.equal(normalizeMediaConfig('C:\\Media Folder\\image#1.png').path, 'file:///C:/Media%20Folder/image%231.png');
});

test('read-only API rejects all mutation paths before making a request', async () => {
    const api = new ReadOnlyApi(59273);
    await assert.rejects(api.get('/restart', new AbortController().signal), /not allowed/);
    await assert.rejects(api.get('/plugins/msi-psu/fan', new AbortController().signal), /not allowed/);
    assert.throws(() => new ReadOnlyApi(0));
    assert.equal(api.socketUrl(), 'ws://localhost:59273/sensors');
});

test('settings keep valid zero/false, clamp values, and only declare documented alphanumeric IDs', () => {
    const settings = new Settings();
    settings.update(name => ({ precision: 0, showPeak: false, panelRadius: 1000, accentColor: 'url(evil)' })[name]);
    assert.equal(settings.number('precision'), 0);
    assert.equal(settings.enabled('showPeak'), false);
    assert.equal(settings.number('panelRadius'), 24);
    assert.equal(settings.text('accentColor'), '#27d8bd');
    assert.equal(new Set(definitions.map(item => item.id)).size, definitions.length);
    assert.ok(definitions.every(item => /^[a-zA-Z][a-zA-Z0-9]*$/.test(item.id)));
    assert.equal(definitions.at(-1).group, 'Widget Personalization');
});

test('fixture enum strings agree with the real service contracts', async () => {
    const frame = fixture();
    for (const [file, values] of [['SensorComponent', frame.sensors.map(item => item.identity.component)],
        ['SensorMetric', frame.sensors.map(item => item.identity.metric)], ['SensorUnit', frame.sensors.map(item => item.unit)]]) {
        const source = await readFile(new URL(`../../Common/Contracts/Enums/${file}.cs`, import.meta.url), 'utf8');
        for (const value of new Set(values)) assert.match(source, new RegExp(`\\b${value}\\b`));
    }
});
