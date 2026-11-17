import assert from 'node:assert/strict';
import { test } from 'node:test';

import { decodeFrame, decodePairingCode, decodeSavedComputer, requestPairing } from '../src/protocol.ts';
import { boostLimitRows, groupMetrics, metricLabel, formatReading, wireViewFaults } from '../src/metrics.ts';
import { destinations, retryDelay, sessionResult } from '../src/connection.ts';
import { decodeIncident, decodeIncidentPage } from '../src/incidentProtocol.ts';
import { WarningEvents } from '../src/warningEvents.ts';

const now = Date.now();
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
    title: 'Current imbalance', phase: 'Active', episodeId: 'episode', pluginId: 'wireview-pro2',
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
  assert.deepEqual(events.receive(frame(3, true, 2)), [{ title: 'Real warning', detail: 'detail', simulation: false }]);
  assert.deepEqual(events.receive(frame(4, true, 2)), []);
});

test('prioritizes paired routes and bounds reconnect delays', () => {
  const computer = decodePairingCode(JSON.stringify(invite), now);
  assert.deepEqual(destinations(computer, 'auto', false).map(target => target.route), ['local', 'remote']);
  assert.deepEqual(destinations(computer, 'auto', true).map(target => target.route), ['remote', 'local']);
  assert.deepEqual(destinations(computer, 'remote', false).map(target => target.route), ['remote']);
  assert.equal(retryDelay(100), 30_000);
  assert.equal(retryDelay(1), 1_000);
  assert.equal(sessionResult({ status: 401 }), 'revoked');
  assert.equal(sessionResult({ status: 0, keyMismatch: true }), 'changed-key');
  assert.equal(sessionResult({ status: 204 }), 'connect');
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
