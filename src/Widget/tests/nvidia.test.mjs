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

test('a disabled GenericNvidia source cannot silently switch to ASUS', () => {
    const { store, present } = context();
    store.configuration.configuration.plugins['generic-nvidia'].enabled = false;
    assert.equal(present().pluginId, 'generic-nvidia');
    assert.equal(present().state, 'Disabled');
    assert.equal(present().coreVoltage, '-');
});

test('GPU status shows core and VRAM temperatures with one decimal even when hotspot is reported', () => {
    for (const precision of ['0', '1', '2']) {
        const temperatures = context({ precision, showHotspot: true }).present().rows
            .filter(row => row.id.startsWith('temperature'));
        assert.deepEqual(temperatures.map(row => row.text), ['62.0 °C', '70.0 °C']);
    }
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
