import test from 'node:test';
import assert from 'node:assert/strict';
import { fixture, configuration, catalog } from './nvidia-fixtures.mjs';
import { parseSnapshot } from '../src/telemetry/parseSnapshot.ts';
import { TelemetryStore } from '../src/telemetry/TelemetryStore.ts';
import { Settings } from '../src/settings/Settings.ts';
import { Formatter } from '../src/formatting/Formatter.ts';
import { PeakStore } from '../src/telemetry/PeakStore.ts';
import { PanelPresenter } from '../src/panels/PanelPresenter.ts';

test('GPU status uses backend profile sources and does not fall back locally when unavailable', () => {
    const frame = parseSnapshot(fixture());
    const store = new TelemetryStore();
    store.configuration = configuration();
    store.configuration.configuration.monitoring.connectorProfiles = { version: 1 };
    store.catalog = catalog().plugins;
    store.connection = 'live';

    const power = frame.sensors.find(reading => reading.identity.pluginId === 'wireview-pro2'
        && reading.identity.component === 'GpuConnector' && reading.identity.metric === 'Power');
    power.value = 321;
    const current = frame.sensors.find(reading => reading.identity.pluginId === 'msi-psu'
        && reading.identity.component === 'PsuConnector' && reading.identity.metric === 'Current');
    current.value = 26.7;
    frame.selections = [power, current].map(reading => ({
        name: `gpu-status/${reading.identity.metric.toLowerCase()}`, state: 'Fallback', sensorKey: reading.identity.key
    }));
    store.update(frame);

    const settings = new Settings();
    settings.update(name => name === 'numberLocale' ? 'en-GB' : undefined);
    const presenter = new PanelPresenter(store, settings, new Formatter(settings), new PeakStore());
    const binding = { pluginId: 'generic-nvidia', deviceId: store.device('generic-nvidia', '', Date.parse(frame.timestamp)), kind: 'status' };
    const shown = presenter.present(binding, Date.parse(frame.timestamp));
    assert.match(shown.summary[0].text, /321/);
    assert.match(shown.summary[1].text, /26[.,]7/);
    assert.equal(shown.summary[2].text, '-');

    frame.selections = [];
    store.update(frame);
    assert.equal(presenter.present(binding, Date.parse(frame.timestamp)).summary[0].text, '-');
    assert.equal(presenter.present(binding, Date.parse(frame.timestamp) + 5001).summary[1].text, '-');
});

test('5080 profile scales an associated WireView chart and marks readings above 6 A', () => {
    const frame = fixture();
    const now = Date.parse(frame.timestamp);
    const wireView = frame.sensors.find(reading => reading.identity.pluginId === 'wireview-pro2'
        && reading.identity.component === 'GpuPin' && reading.identity.metric === 'Current');
    wireView.value = 6.25;
    const deviceId = wireView.identity.deviceId;
    frame.settingsProfile = {
        gpuDeviceId: 'gpu-5080', wireViewDeviceId: deviceId,
        profile: { name: 'RTX 5080', gpuModel: '5080', currentCeiling: 6, powerCeilingPerPin: 75,
            powerFallbackPerPin: 100, voltageMinimum: 11.4, voltageMaximum: 12.6, useWireViewOvercurrentFallback: true, overcurrentAmperes: 6 }
    };
    const store = new TelemetryStore();
    store.configuration = configuration();
    store.catalog = catalog().plugins;
    store.connection = 'live';
    store.update(parseSnapshot(frame));
    const settings = new Settings();
    const overrides = { numberLocale: 'en-GB', currentMax: 10, wireviewMetric: 'A' };
    settings.update(name => overrides[name]);
    const presenter = new PanelPresenter(store, settings, new Formatter(settings), new PeakStore());
    const binding = { pluginId: 'wireview-pro2', deviceId, kind: 'pins' };
    const shown = presenter.present(binding, now);
    const pin = shown.pins[wireView.identity.channel - 1];

    assert.match(shown.axis[0], /^6(?:[.,]0+)?\s*A$/);
    assert.equal(pin.number, 6.25);
    assert.equal(pin.fill, 100);
    assert.equal(pin.overflow, true);

    overrides.overrideCurrentMax = true;
    settings.update(name => overrides[name]);
    assert.match(presenter.present(binding, now).axis[0], /^10(?:[.,]0+)?\s*A$/);
    assert.equal(presenter.present(binding, now).pins[wireView.identity.channel - 1].number, 6.25);

    overrides.wireviewMetric = 'W';
    settings.update(name => overrides[name]);
    assert.match(presenter.present(binding, now).axis[0], /^75(?:[.,]0+)?\s*W$/);

    overrides.powerCableFallback = 300;
    overrides.overridePowerFallback = true;
    settings.update(name => overrides[name]);
    assert.match(presenter.present(binding, now).axis[0], /^75(?:[.,]0+)?\s*W$/);
    frame.settingsProfile.profile.powerCeilingPerPin = null;
    frame.sensors = frame.sensors.filter(reading => reading.identity.metric !== 'PowerCapability');
    store.update(parseSnapshot(frame));
    assert.match(presenter.present(binding, now).axis[0], /^50(?:[.,]0+)?\s*W$/);
    overrides.overridePowerFallback = false;
    settings.update(name => overrides[name]);
    assert.match(presenter.present(binding, now).axis[0], /^100(?:[.,]0+)?\s*W$/);

    frame.settingsProfile.wireViewDeviceId = null;
    store.update(parseSnapshot(frame));
    overrides.wireviewMetric = 'A';
    settings.update(name => overrides[name]);
    assert.match(presenter.present(binding, now).axis[0], /^10(?:[.,]0+)?\s*A$/);
});
