import test from 'node:test';
import assert from 'node:assert/strict';
import { Settings } from '../src/settings/Settings.ts';
import { TelemetryStore } from '../src/telemetry/TelemetryStore.ts';
import { parseSnapshot } from '../src/telemetry/parseSnapshot.ts';
import { PanelPresenter } from '../src/panels/PanelPresenter.ts';
import { resolvePanel } from '../src/panels/resolvePanel.ts';
import { Formatter } from '../src/formatting/Formatter.ts';
import { PeakStore } from '../src/telemetry/PeakStore.ts';
import { fixture, configuration, catalog } from './nvidia-fixtures.mjs';

function context(overrides = {}, frame = fixture()) {
    const settings = new Settings();
    settings.update(name => ({ panel1: 'nvidiaStatus', numberLocale: 'en-GB', ...overrides })[name]);
    const store = new TelemetryStore();
    store.update(parseSnapshot(frame));
    store.connection = 'live';
    store.configuration = configuration();
    store.catalog = catalog().plugins;
    const formatter = new Formatter(settings);
    formatter.configure('°C');
    const presenter = new PanelPresenter(store, settings, formatter, new PeakStore());
    const now = Date.parse(frame.timestamp);
    return { store, settings, now, present: () => presenter.present(resolvePanel(1, settings, store, now), now) };
}

test('legacy GPU status selections migrate to GenericNvidia without changing pin selections', () => {
    const { settings, present } = context({ panel1: 'asusStatus' });
    assert.equal(settings.text('panel1'), 'nvidiaStatus');
    assert.equal(present().pluginId, 'generic-nvidia');
    assert.equal(present().gpuUsage, '97%');
    assert.equal(present().coreVoltage, '925.0 mV');
    assert.equal(present().rows.length, 7);
    assert.equal(present().vramUsagePercent, 75);
});

test('GPU status and Astral pins follow their own freshness and enablement settings', () => {
    const { store, present } = context();
    const plugins = store.configuration.configuration.plugins;
    plugins['generic-nvidia'].staleAfterMs = 5000;
    plugins['asus-astral'].staleAfterMs = 2200;
    plugins['asus-astral'].enabled = false;
    assert.equal(store.staleAfter('generic-nvidia'), 5000);
    assert.equal(store.staleAfter('asus-astral'), 2200);
    assert.equal(present().state, 'Ready');
});

test('a disabled GenericNvidia source cannot silently switch to ASUS', () => {
    const { store, present } = context();
    store.configuration.configuration.plugins['generic-nvidia'].enabled = false;
    assert.equal(present().pluginId, 'generic-nvidia');
    assert.equal(present().state, 'Disabled');
    assert.equal(present().coreVoltage, '-');
});

test('GPU temperature columns follow the hotspot preference and the selected temperature unit', () => {
    const temperatures = (settings, frame) => context(settings, frame).present().rows.filter(row => row.id.startsWith('temperature'));
    assert.deepEqual(temperatures({}).map(row => row.label), ['Core', 'VRAM']);
    for (const precision of ['0', '1', '2']) {
        const rows = temperatures({ precision, showHotspotTemperature: true });
        assert.deepEqual(rows.map(row => row.label), ['Core', 'VRAM', 'Hotspot']);
        assert.deepEqual(rows.map(row => row.text), ['62.0 °C', '70.0 °C', '73.3 °C']);
    }
    const enabled = { showHotspotTemperature: true, temperatureUnit: '°F' };
    assert.equal(temperatures(enabled).at(-1).text, '163.9 °F');
    const frame = fixture();
    frame.sensors = frame.sensors.filter(reading => reading.identity.component !== 'GpuHotspot');
    assert.equal(temperatures(enabled, frame).at(-1).text, '-');
});

test('fan duty follows its RPM and can be hidden without changing fan labels', () => {
    const fans = data => data.rows.filter(row => row.id.startsWith('fan'));
    const rows = fans(context().present());
    assert.deepEqual(rows.map(row => row.label), ['Fan 1', 'Fan 2']);
    assert.deepEqual(rows.map(row => row.text), ['1220 RPM (50%)', '1185 RPM (45%)']);
    assert.deepEqual(fans(context({ showFanDuty: false }).present()).map(row => row.text), ['1220 RPM', '1185 RPM']);

    const frame = fixture();
    frame.sensors = frame.sensors.filter(reading => reading.identity.pluginId !== 'generic-nvidia'
        || reading.identity.component !== 'GpuFan' || reading.identity.channel !== 2);
    const singleFan = fans(context({}, frame).present());
    assert.deepEqual(singleFan.map(row => row.label), ['Fan 1']);
    assert.equal(singleFan[0].text, '1220 RPM (50%)');
});

test('optional internal clocks are independently scoped to reported sensors on the selected GPU', () => {
    const flags = { showCrossbarClock: true, showSystemClock: true };
    const domain = data => data.rows.filter(row => ['GpuSystem', 'GpuCrossbar'].includes(row.id));
    assert.deepEqual(domain(context().present()), []);
    assert.deepEqual(domain(context({ showSystemClock: true }).present()).map(row => row.text), ['1650 MHz']);
    const frame = fixture();
    frame.sensors.find(sensor => sensor.identity.component === 'GpuCrossbar').identity.deviceId = 'other';
    const sys = frame.sensors.find(sensor => sensor.identity.component === 'GpuSystem');
    sys.value = null;
    sys.quality = 'Unavailable';
    assert.deepEqual(domain(context(flags, frame).present()).map(row => [row.id, row.text]), [['GpuSystem', '-']]);
});

test('hiding GPU fans shows the reported internal clocks even with both clock preferences off', () => {
    const frame = fixture();
    const options = { hideGpuFans: true };
    for (const remaining of [['GpuCrossbar', 'GpuSystem'], ['GpuSystem'], []]) {
        frame.sensors = frame.sensors.filter(sensor => !['GpuCrossbar', 'GpuSystem'].includes(sensor.identity.component)
            || remaining.includes(sensor.identity.component));
        const rows = context(options, frame).present().rows;
        assert.deepEqual(rows.filter(row => row.id.startsWith('Gpu')).map(row => row.id), remaining);
        assert.ok(!rows.some(row => row.id.startsWith('fan')));
    }
});

test('input values are measured, with WireView association only when explicitly configured', () => {
    const frame = fixture();
    frame.sensors.find(reading => reading.identity.pluginId === 'wireview-pro2'
        && reading.identity.component === 'GpuConnector' && reading.identity.metric === 'Power').value = 500;
    const { store, present } = context({}, frame);
    assert.equal(present().summary[0].text, '432.0 W');
    store.configuration.configuration.plugins['generic-nvidia'].options.electricalSource = 'WireView';
    assert.equal(present().summary[0].text, '500.0 W');
    store.configuration.configuration.plugins['generic-nvidia'].options.electricalSource = 'None';
    assert.ok(present().summary.every(metric => metric.text === '-'));

    frame.sensors = frame.sensors.filter(reading => reading.identity.pluginId !== 'generic-nvidia'
        || reading.identity.component !== 'GpuConnector' || reading.identity.metric !== 'Voltage');
    assert.equal(context({}, frame).present().summary[2].text, '-');
    assert.ok(!present().rows.some(row => row.id === 'boardPower'));
});
