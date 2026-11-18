import assert from 'node:assert/strict';
import { test } from 'node:test';

import { decodeFrame, decodePairingCode, decodeSavedComputer, requestPairing } from '../src/protocol.ts';
import { boostLimitRows, groupMetrics, metricLabel, formatReading, pinPanelSources, powerConnectorGroups, wireViewFaults } from '../src/metrics.ts';
import { destinations, localSwitchAddress, retryDelay, sessionResult } from '../src/connection.ts';
import { decodeIncident, decodeIncidentPage, warningNotificationIncident } from '../src/incidentProtocol.ts';
import {
  allowsWarning, decodeDisplayPreferences, decodeNotificationPreferences, defaultDisplayPreferences, homePanelKey, moveHomePanel,
  warningCategories, warningCategory,
} from '../src/mobilePreferences.ts';
import { decodeAppearance, defaultAppearance, schemes, selectedPalette, themeColors } from '../src/appearance.ts';
import { hexToHsv, hsvToHex } from '../src/colorValues.ts';
import { homeDropIndex } from '../src/homeDropTarget.ts';
import { WarningEvents } from '../src/warningEvents.ts';

const now = Date.now();

test('opens only a valid warning or Safeguard+ incident carried by a notification', () => {
  const data = { screen: 'warnings', kind: 'warning', incidentId: '08B8CD72-E449-43DD-A959-8418DC239F51' };
  assert.deepEqual(warningNotificationIncident(data), { id: data.incidentId.toLowerCase(), kind: 'warning' });
  assert.equal(warningNotificationIncident({ ...data, incidentId: '/session' }), null);
  assert.deepEqual(warningNotificationIncident({ ...data, kind: 'safeguard' }), { id: data.incidentId.toLowerCase(), kind: 'safeguard' });
  assert.equal(warningNotificationIncident({ screen: 'warnings' }), null);
});
const invite = {
  schemaVersion: 1,
  localAddress: 'https://192.168.1.30:59275/',
  remoteAddress: 'https://rtxlens-test.ddns.net:59275/',
  publicKeyFingerprint: 'A'.repeat(64),
  secret: 'b'.repeat(43),
  expiresAt: new Date(now + 300000).toISOString(),
};

test('accepts the desktop invite and normalizes its addresses and fingerprint', () => {
  const code = decodePairingCode(JSON.stringify(invite), now);
  assert.equal(code.localAddress, 'https://192.168.1.30:59275');
  assert.equal(code.remoteAddress, 'https://rtxlens-test.ddns.net:59275');
  assert.equal(code.publicKeyFingerprint, 'a'.repeat(64));
});

test('rejects an expired code and unsafe or ambiguous endpoint syntax', () => {
  assert.throws(() => decodePairingCode(JSON.stringify(invite), now + 300001), /expired/);

  for (const remoteAddress of ['http://example.net:59275', 'https://name@evil.net:59275', 'https://example.net:59275/admin']) {
    assert.throws(() => decodePairingCode(JSON.stringify({ ...invite, remoteAddress }), now), /invalid/);
  }

  assert.throws(() => decodePairingCode(JSON.stringify({ ...invite, localAddress: 'https://256.1.1.2:59275' }), now), /invalid/);
});

test('pairs with the approved claim and restores the saved device credential', async () => {
  const code = decodePairingCode(JSON.stringify(invite), now);
  const paths = [];
  const id = '12345678-1234-1234-1234-123456789abc';
  const token = 'a'.repeat(32) + '.' + 'd'.repeat(43);
  const computer = await requestPairing(code, 'Test phone', async (path, json) => {
    paths.push(path);
    const payload = JSON.parse(json);
    if (path === '/pairing') {
      assert.equal(payload.invite, code.secret);
      return { status: 200, body: JSON.stringify({ id, secret: 'c'.repeat(43) }) };
    }
    assert.equal(payload.id, id);
    return { status: 200, body: JSON.stringify({ token }) };
  }, () => {}, new AbortController().signal);

  assert.deepEqual(paths, ['/pairing', '/pairing/complete']);
  assert.deepEqual(decodeSavedComputer(JSON.stringify(computer)), computer);
  assert.equal(decodeSavedComputer(JSON.stringify({ ...computer, token: 'invalid' })), null);
});

test('decodes a versioned frame and ignores malformed readings', () => {
  const sensor = {
    identity: {
      key: 'power', pluginId: 'wireview-pro2', deviceId: 'wireview-1', connectorId: null, component: 'GpuConnector', metric: 'Power', channel: 1,
    },
    unit: 'Watts',
    value: 120,
    quality: 'Valid',
  };
  const warnings = [{
    title: 'Current imbalance', phase: 'Active', episodeId: 'episode', pluginId: 'wireview-pro2', icon: 'imbalance',
    detail: 'Pin 2 exceeded the current limit', startedAt: new Date().toISOString(), notificationSequence: 1,
  }];
  const frame = {
    schemaVersion: 1, sequence: 1, timestamp: new Date().toISOString(), sensors: [sensor, {}], warnings: { states: warnings },
  };
  assert.deepEqual(decodeFrame(JSON.stringify(frame))?.sensors, [sensor]);
  assert.deepEqual(decodeFrame(JSON.stringify(frame))?.warnings, warnings);
  const wholeGpu = { ...sensor, identity: { ...sensor.identity, channel: null } };
  assert.deepEqual(decodeFrame(JSON.stringify({ ...frame, sensors: [wholeGpu] }))?.sensors, [wholeGpu]);
  assert.equal(decodeFrame(JSON.stringify({ ...frame, schemaVersion: 2 })), null);
});

test('keeps pin measurements from different devices and PSU connectors distinguishable', () => {
  const pin = (deviceId, connectorId, channel) => ({
    identity: { key: `${deviceId}/${connectorId}/${channel}`, pluginId: 'msi-psu', deviceId, connectorId,
      component: 'PsuPin', metric: 'Current', channel },
    unit: 'Amperes', value: 4.1, quality: 'Valid',
  });
  const first = pin('first', '1', 1);
  const second = pin('first', '2', 1);
  const groups = groupMetrics([first, second, pin('second', '1', 1)], 'power');
  assert.equal(groups.length, 2);
  assert.deepEqual(groups[0].readings.map(reading => reading.identity.connectorId), ['1', '2']);
  assert.deepEqual(groupMetrics([first], 'gpu'), []);
  assert.equal(formatReading({ ...first, value: 4 }), '4 A');
  assert.equal(metricLabel({ ...first, identity: { ...first.identity, component: 'GpuFan', metric: 'FanSpeed', channel: 2 } }), 'Fan 2');
});

test('uses the service notification sequence and deduplicates simulation warnings independently', () => {
  const events = new WarningEvents();
  const startedAt = new Date().toISOString();
  const warning = { episodeId: 'episode', title: 'Real warning', detail: 'detail', phase: 'Active', notificationSequence: 1 };
  const simulation = { id: 'test', title: 'Test warning', detail: 'detail', startedAt };
  const frame = (sequence, notificationsEnabled, notificationSequence) => decodeFrame(JSON.stringify({
    schemaVersion: 1, sequence, timestamp: startedAt, sensors: [], simulationWarnings: [simulation, {}],
    warnings: { instanceId: 'service', notificationsEnabled, states: [{ ...warning, notificationSequence }] },
  }));

  assert.deepEqual(events.receive(frame(1, false, 1)), [{ title: 'Test warning', detail: 'detail', simulation: true }]);
  assert.deepEqual(events.receive(frame(2, true, 1)), []);
  assert.deepEqual(events.receive(frame(3, true, 2)), [{ title: 'Real warning', detail: 'detail', simulation: false, category: warningCategories.other }]);
  assert.deepEqual(events.receive(frame(4, true, 2)), []);
});

test('keeps default Home cards and restores custom selections including an empty dashboard', () => {
  assert.deepEqual(decodeDisplayPreferences('{"layoutVersion":2,"homePanels":[]}').homePanels, []);
  assert.deepEqual(decodeDisplayPreferences('{}'), defaultDisplayPreferences);
  assert.deepEqual(decodeDisplayPreferences('{"visibleHomeCards":[],"hideIdleMsiConnectors":true}'), {
    homePanels: [{ kind: 'metric', id: 'status' }], hideIdleMsiConnectors: true,
  });
  assert.deepEqual(decodeDisplayPreferences('{"visibleHomeCards":["activeWarnings","coreTemperature","missing","activeWarnings"]}'), {
    homePanels: [{ kind: 'metric', id: 'status' }, { kind: 'metric', id: 'coreTemperature' }, { kind: 'metric', id: 'activeWarnings' }], hideIdleMsiConnectors: false,
  });
});

test('restores reordered metric and pin panels even when the pin source is unavailable', () => {
  const pin = { kind: 'pins', pluginId: 'msi-psu', deviceId: 'psu1', connectorId: '2' };
  const panels = [
    { kind: 'metric', id: 'gpuUsage' }, pin, { kind: 'metric', id: 'activeWarnings' },
  ];
  const reordered = moveHomePanel(panels, 1, 0);
  assert.deepEqual(reordered, [pin, panels[0], panels[2]]);
  assert.deepEqual(moveHomePanel(reordered, 1, 2), [pin, panels[2], panels[0]]);
  assert.deepEqual(moveHomePanel(reordered, 0, -1), reordered);
  assert.deepEqual(moveHomePanel(reordered, 2, 3), reordered);
  assert.deepEqual(decodeDisplayPreferences(JSON.stringify({ layoutVersion: 2, homePanels: [...reordered, pin, { kind: 'metric', id: 'unknown' }] })).homePanels, reordered);
});

test('builds independent pin panels for each reporting plugin, device, and connector', () => {
  const reading = (pluginId, deviceId, connectorId, component = 'GpuPin', metric = 'Current') => ({
    identity: { pluginId, deviceId, connectorId, component, metric }, value: 1, quality: 'Valid',
  });
  const asus = reading('asus-astral', 'gpu1', null);
  const wireView = reading('wireview-pro2', 'wv1', null);
  const msi1 = reading('msi-psu', 'psu1', '1', 'PsuPin');
  const msi2 = reading('msi-psu', 'psu1', '2', 'PsuPin');
  const otherPsu = reading('msi-psu', 'psu2', '1', 'PsuPin');
  const board = reading('wireview-pro2', 'wv1', null, 'MonitorBoard', 'Temperature');
  const sources = pinPanelSources(groupMetrics([asus, wireView, msi1, msi2, otherPsu, board], 'power'));
  assert.equal(sources.size, 5);
  for (const pin of [asus, wireView, msi1, msi2, otherPsu]) {
    const { pluginId, deviceId, connectorId } = pin.identity;
    const key = homePanelKey({ kind: 'pins', pluginId, deviceId, connectorId });
    assert.deepEqual(sources.get(key)?.pins, [pin]);
  }
});

test('hides only MSI connectors with a valid zero total current', () => {
  const reading = (connectorId, value, quality = 'Valid', component = 'PsuConnector') => ({
    identity: { connectorId, component, metric: 'Current' }, value, quality,
  });
  const zero = reading('1', 0);
  const draw = reading('2', 0.01);
  const device = reading(null, 25, 'Valid', 'Psu');
  const group = { source: 'msi-psu', readings: [zero, reading('1', 0, 'Valid', 'PsuPin'), draw, device] };
  const ids = (group, hide) => powerConnectorGroups(group, hide).map(([id]) => id);

  assert.deepEqual(ids(group, true), ['2', null]);
  assert.deepEqual(ids(group, false), ['1', '2', null]);
  assert.deepEqual(ids({ ...group, source: 'wireview-pro2' }, true), ['1', '2', null]);
  assert.deepEqual(ids({ ...group, readings: [reading('1', 0, 'Stale'), reading('2', null), device] }, true), ['1', '2', null]);
  assert.deepEqual(ids({ ...group, readings: [reading('1', 0.01), draw, device] }, true), ['1', '2', null]);
});

test('filters temperature separately from connector faults and rejects invalid stored flags', () => {
  const preferences = decodeNotificationPreferences('{"categories":2,"showTitle":false}');
  assert.equal(preferences.showTitle, false);
  assert.equal(allowsWarning(preferences, warningCategory('wireview-pro2', 'temperature')), true);
  assert.equal(allowsWarning(preferences, warningCategory('wireview-pro2', 'current')), false);
  assert.equal(allowsWarning(preferences, warningCategories.safeguard), false);
  assert.throws(() => decodeNotificationPreferences('{"categories":16,"showTitle":false}'));
});

test('recognizes new Safeguard+ events without replaying history or simulations', () => {
  const events = new WarningEvents();
  const timestamp = new Date(now).toISOString();
  const historical = { id: '08b8cd72-e449-43dd-a959-8418dc239f51', connector: 1, startedAt: new Date(now - 10_000).toISOString() };
  const triggered = { id: '08b8cd72-e449-43dd-a959-8418dc239f52', connector: 2, startedAt: timestamp };
  const simulated = { ...triggered, id: '08b8cd72-e449-43dd-a959-8418dc239f53', isSimulation: true };
  const frame = (sequence, safeguardExecutions) => decodeFrame(JSON.stringify({
    schemaVersion: 1, sequence, timestamp, sensors: [], safeguardExecutions,
  }));
  assert.deepEqual(events.receive(frame(1, [historical])), []);
  const notices = events.receive(frame(2, [historical, triggered, simulated]));
  assert.equal(notices.length, 1);
  assert.equal(notices[0].category, warningCategories.safeguard);
  assert.deepEqual(notices[0].incident, { id: triggered.id, kind: 'safeguard' });
  assert.deepEqual(events.receive(frame(3, [historical, triggered, simulated])), []);
});

test('formats all Celsius readings using the selected temperature unit', () => {
  const reading = { identity: { component: 'MonitorBoard', metric: 'Temperature' }, quality: 'Valid', unit: 'Celsius', value: 30 };
  assert.equal(formatReading(reading), '30 °C');
  assert.equal(formatReading(reading, 'fahrenheit'), '86 °F');
  assert.equal(formatReading({ ...reading, value: 0 }, 'fahrenheit'), '32 °F');
  assert.equal(formatReading({ ...reading, value: null }, 'fahrenheit'), 'Unavailable');
});

test('prioritizes paired routes and bounds reconnect delays', () => {
  const computer = decodePairingCode(JSON.stringify(invite), now);
  assert.deepEqual(destinations(computer), [
    { route: 'local', address: computer.localAddress }, { route: 'remote', address: computer.remoteAddress },
  ]);
  assert.deepEqual(destinations({ ...computer, remoteAddress: computer.localAddress }), [
    { route: 'local', address: computer.localAddress },
  ]);
  assert.equal(retryDelay(100), 30_000);
  assert.equal(retryDelay(1), 1_000);
  assert.equal(sessionResult({ status: 401 }), 'revoked');
  assert.equal(sessionResult({ status: 0, keyMismatch: true }), 'changed-key');
  assert.equal(sessionResult({ status: 204 }), 'connect');
});

test('only offers a switch from a distinct remote connection', () => {
  const computer = decodePairingCode(JSON.stringify(invite), now);
  const [local, remote] = destinations(computer);
  assert.equal(localSwitchAddress(computer, remote), computer.localAddress);
  assert.equal(localSwitchAddress(computer, local), null);
  assert.equal(localSwitchAddress(computer, null), null);
  assert.equal(localSwitchAddress(computer, { ...remote, address: computer.localAddress }), null);
});

test('matches WireView fault flags and keeps zero boost durations distinguishable', () => {
  const reading = (component, metric, value, channel = null) => ({
    identity: { component, metric, channel }, value, unit: 'Count', quality: value === null ? 'Unavailable' : 'Valid',
  });
  assert.deepEqual(wireViewFaults([
    reading('MonitorBoard', 'FaultStatus', 0x0a), reading('MonitorBoard', 'FaultLog', 0x28),
  ]), [
    { name: 'Overtemperature', status: 'Active' },
    { name: 'Wire overcurrent', status: 'Active / Logged' },
    { name: 'Current imbalance', status: 'Logged' },
  ]);
  assert.deepEqual(boostLimitRows([
    reading('GpuPerformance', 'ActiveClockReasons', 4), reading('GpuPerformance', 'SupportedClockReasons', 5),
    reading('GpuPerformance', 'ClockLimitDuration', 0, 4),
  ]), [{ name: 'GPU idle', active: false, seconds: null }, { name: 'Software power cap', active: true, seconds: 0 }]);
});

test('validates incident pagination and rejects mismatched reports', () => {
  const incident = {
    id: '12345678-1234-1234-1234-123456789abc', kind: 'safeguard', title: 'Test report',
    source: 'Safeguard+', status: 'Simulated', startedAt: new Date(now).toISOString(), connector: 2,
  };
  const page = decodeIncidentPage(JSON.stringify({ items: [incident], next: null }));
  assert.equal(page.items[0].connector, 2);
  const report = decodeIncident(JSON.stringify({ ...incident, delayMs: 0, steps: [{ name: 'Test', result: 'OK', exitCode: 0 }] }), page.items[0]);
  assert.equal(report.delayMs, 0);
  assert.equal(report.steps[0].exitCode, 0);
  assert.throws(() => decodeIncident(JSON.stringify({ ...incident, kind: 'warning' }), page.items[0]), /invalid incident/);
  assert.throws(() => decodeIncidentPage('{"items":[],"next":"../configuration"}'), /invalid history/);
});

test('restores desktop schemes in both modes and validates custom colors', () => {
  for (const [scheme, dark, light] of schemes) {
    for (const mode of [true, false]) {
      const appearance = { ...defaultAppearance, scheme, light: mode };
      assert.deepEqual(decodeAppearance(JSON.stringify(appearance)), appearance);
      assert.equal(selectedPalette(appearance).primary, (mode ? light : dark).split(' ')[0]);
    }
  }
  const custom = { primary: '#FFFFFF', secondary: '#FFFFFF', tertiary: '#FFFFFF', background: '#FFFFFF' };
  const value = { ...defaultAppearance, scheme: 'Custom', custom };
  assert.notEqual(themeColors(value).accent, custom.background);
  assert.deepEqual(decodeAppearance(JSON.stringify({ ...value, custom: { ...custom, primary: 'invalid' } })), defaultAppearance);
});

test('targets the original slots for drops between tiles and around full-width panels', () => {
  const slots = [
    { x: 0, y: 0, width: 300, height: 150 },
    { x: 0, y: 160, width: 145, height: 80 },
    { x: 155, y: 160, width: 145, height: 80 },
    { x: 0, y: 250, width: 300, height: 200 },
  ];
  assert.equal(homeDropIndex(slots, 200, 205, 0), 2);
  assert.equal(homeDropIndex(slots, 150, 205, 2), 2);
  assert.equal(homeDropIndex(slots, 50, 248, 1), 3);
  assert.equal(homeDropIndex(slots, -20, -20, 2), 0);
});

test('round-trips custom colors through the picker including grayscale and red at both ends of the hue strip', () => {
  for (const hex of ['#39EED1', '#236872', '#7C57CE', '#000000', '#777777', '#FFFFFF', '#FF0100', '#FF0001']) {
    assert.equal(hsvToHex(hexToHsv(hex)), hex);
  }
  assert.equal(hsvToHex({ hue: 0, saturation: 1, brightness: 1 }), '#FF0000');
  assert.equal(hsvToHex({ hue: 360, saturation: 1, brightness: 1 }), '#FF0000');
});
