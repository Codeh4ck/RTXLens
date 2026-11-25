import test from 'node:test';
import assert from 'node:assert/strict';
import { Settings } from '../src/settings/Settings.ts';
import { definitions } from '../src/settings/definitions.ts';
import { Formatter } from '../src/formatting/Formatter.ts';
import { TelemetryStore } from '../src/telemetry/TelemetryStore.ts';
import { parseSnapshot } from '../src/telemetry/parseSnapshot.ts';
import { PeakStore } from '../src/telemetry/PeakStore.ts';
import { PanelPresenter } from '../src/panels/PanelPresenter.ts';
import { resolvePanel } from '../src/panels/resolvePanel.ts';
import { fixture, configuration, catalog } from './fixtures.mjs';

test('decimal dropdown accepts all choices and preserves numeric slider preferences', () => {
    const definition = definitions.find(item => item.id === 'precision');
    assert.equal(definition.type, 'combobox');
    assert.equal(definition.defaultValue, '1');
    assert.deepEqual(definition.options.map(option => option.key), ['0', '1', '2']);
    for (const precision of [0, 1, 2, '0', '1', '2']) {
        const settings = new Settings();
        settings.update(name => ({ precision, numberLocale: 'en-GB' })[name]);
        const format = new Formatter(settings);
        format.configure('°C');
        assert.equal(settings.number('precision'), Number(precision));
        assert.equal(format.value(8.24, 'A'), ['8 A', '8.2 A', '8.24 A'][Number(precision)]);
        assert.equal(format.value(.4, 'A'), ['0 A', '0.4 A', '0.40 A'][Number(precision)]);
        assert.equal(format.value(0, 'A'), ['0 A', '0.0 A', '0.00 A'][Number(precision)]);
        assert.equal(format.value(12, 'V'), ['12 V', '12.0 V', '12.00 V'][Number(precision)]);
        assert.equal(format.temperature(62.34), '62.3 °C');
        assert.equal(format.value(14001, 'MHz', 0), '14001 MHz');
    }
    const settings = new Settings();
    settings.update(name => name === 'precision' ? 1.5 : undefined);
    assert.equal(settings.number('precision'), 1);
});

test('widget clock uses local hours and handles midnight and noon in either format', () => {
    const settings = new Settings();
    const formatter = new Formatter(settings);
    assert.equal(settings.enabled('showWidgetClock'), false);
    for (const [hour, full, half] of [[0, '00:05', '12:05 AM'], [12, '12:05', '12:05 PM'], [13, '13:05', '01:05 PM']]) {
        const timestamp = new Date(2026, 0, 1, hour, 5).getTime();
        for (const [format, expected] of [['24h', full], ['12h', half]]) {
            settings.update(key => key === 'widgetClockFormat' ? format : undefined);
            assert.equal(formatter.localTime(timestamp), expected);
        }
    }
});

test('trailing zeros reach every producer, PSU comparison, headroom and summary without changing raw values', () => {
    assert.ok(!definitions.some(definition => definition.id === 'barSmoothing'));
    const settings = new Settings();
    settings.update(name => ({ precision: '2', panel1: 'asusPins', panel2: 'wireviewPins', panel3: 'msiConnector1',
        asusCompare: true, wireviewCompare: true, numberLocale: 'en-GB' })[name]);
    const snapshot = fixture();
    snapshot.sensors.filter(reading => ['GpuPin', 'PsuPin'].includes(reading.identity.component)
        && reading.identity.metric === 'Current').forEach(reading => { reading.value = .4; });
    const store = new TelemetryStore();
    store.update(parseSnapshot(snapshot));
    store.configuration = configuration();
    store.catalog = catalog().plugins;
    store.connection = 'live';
    store.wireLimit = 9.2;
    store.wireLimitDevice = 'com3';
    const format = new Formatter(settings);
    format.configure('°C');
    const now = Date.parse(snapshot.timestamp);
    const presenter = new PanelPresenter(store, settings, format, new PeakStore());
    for (const slot of [1, 2, 3]) {
        const binding = resolvePanel(slot, settings, store, now);
        const data = presenter.present(binding, now);
        assert.equal(data.pins[0].value, '0.40 A');
        assert.equal(data.pins[0].number, .4);
        assert.equal(data.pins[0].fill, 4);
        if (slot < 3) {
            assert.equal(data.pins[0].comparison, '0.40 A');
            assert.equal(data.pins[0].headroom, '+8.80 A');
            assert.equal(data.spread, '0.00 A');
            assert.equal(presenter.present({ ...binding, kind: 'status' }, now).summary[0].text, '432.00 W');
        }
    }
    settings.update(name => ({ precision: '2', numberLocale: 'de-DE', showUnits: false })[name]);
    format.configure('°C');
    assert.equal(format.value(.4, 'A'), '0,40');
    assert.equal(format.value(null, 'A'), '-');
    assert.equal(format.value(NaN, 'A'), '-');
});
