import assert from 'node:assert/strict';
import { test } from 'node:test';

import { decodeFrame, decodePairingCode, decodeSavedComputer, requestPairing } from '../src/protocol.ts';

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
    identity: { key: 'power', pluginId: 'wireview-pro2', component: 'Connector', metric: 'Power', channel: 1 },
    unit: 'Watts',
    value: 120,
    quality: 'Valid',
  };
  const warnings = [{ title: 'Current imbalance', phase: 'Active', episodeId: 'episode' }];
  const frame = {
    schemaVersion: 1, sequence: 1, timestamp: new Date().toISOString(), sensors: [sensor, {}], warnings: { states: warnings },
  };
  assert.deepEqual(decodeFrame(JSON.stringify(frame))?.sensors, [sensor]);
  assert.deepEqual(decodeFrame(JSON.stringify(frame))?.warnings, warnings);
  const wholeGpu = { ...sensor, identity: { ...sensor.identity, channel: null } };
  assert.deepEqual(decodeFrame(JSON.stringify({ ...frame, sensors: [wholeGpu] }))?.sensors, [wholeGpu]);
  assert.equal(decodeFrame(JSON.stringify({ ...frame, schemaVersion: 2 })), null);
});
