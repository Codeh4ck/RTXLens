import test from 'node:test';
import assert from 'node:assert/strict';
import { ServiceClient } from '../src/transport/ServiceClient.ts';
import { TelemetryStore } from '../src/telemetry/TelemetryStore.ts';
import { parseSnapshot } from '../src/telemetry/parseSnapshot.ts';
import { fixture, configuration, catalog } from './fixtures.mjs';

async function stream(t, interval) {
    const sockets = [];
    class Socket {
        constructor() { sockets.push(this); }
        close(code) { this.closed = code ?? 1000; this.onclose?.(); }
        frame(sequence, revision = 7) {
            this.onmessage?.({ data: JSON.stringify({ ...fixture(), sequence, configurationRevision: revision }) });
        }
    }
    const original = globalThis.WebSocket;
    globalThis.WebSocket = Socket;
    t.after(() => { globalThis.WebSocket = original; });
    t.mock.timers.enable({ apis: ['setTimeout'] });
    let elapsed = 0;
    t.mock.method(performance, 'now', () => elapsed);
    t.mock.method(Math, 'random', () => 0.5);
    const config = configuration();
    config.configuration.monitoring.streamIntervalMs = interval;
    t.mock.method(globalThis, 'fetch', async (url) => ({
        ok: true, json: async () => new URL(url).pathname === '/plugins' ? catalog() : config
    }));
    const store = new TelemetryStore();
    const client = new ServiceClient(store, () => {});
    t.after(() => client.stop());
    client.start(59273);
    sockets[0].onopen();
    await new Promise(resolve => setImmediate(resolve));
    return { sockets, store, client, advance(ms) { elapsed += ms; t.mock.timers.tick(ms); } };
}

test('a silent open socket reconnects even if close never arrives, and duplicate frames do not postpone it', async t => {
    const { sockets, store, advance } = await stream(t, 1000);
    const first = sockets[0];
    first.frame(10);
    advance(4000);
    first.frame(11);
    advance(1000);
    first.frame(11);
    advance(3999);
    assert.equal(store.connection, 'live');
    advance(1);
    assert.equal(first.closed, 4000);
    assert.equal(store.connection, 'reconnecting');
    assert.match(store.connectionMessage, /no telemetry/i);
    advance(374);
    assert.equal(sockets.length, 1);
    advance(126);
    assert.equal(sockets.length, 2);
    sockets[1].frame(1);
    assert.equal(store.connection, 'live');
    assert.equal(store.snapshot.sequence, 1);
});

test('a slow publisher gets a full silence window after its last valid frame', async t => {
    const { sockets, advance, store } = await stream(t, 30000);
    const socket = sockets[0];
    socket.frame(1);
    advance(30000);
    socket.frame(2);
    advance(59999);
    assert.equal(socket.closed, undefined);
    advance(1);
    assert.equal(store.connection, 'live');
    advance(30000);
    assert.equal(socket.closed, 4000);
});

test('unknown stream cadence on a new configuration revision uses the supported maximum', async t => {
    const { sockets, advance } = await stream(t, 100);
    sockets[0].frame(1, 8);
    advance(5000);
    advance(30000);
    assert.equal(sockets[0].closed, undefined);
    advance(55000);
    assert.equal(sockets[0].closed, 4000);
});

test('suspension and a delayed host timer give telemetry time to resume', async t => {
    const { client, sockets, advance } = await stream(t, 1000);
    const socket = sockets[0];
    client.setSuspended(true);
    advance(120000);
    client.setSuspended(false);
    advance(4999);
    socket.frame(1);
    advance(60000);
    assert.equal(socket.closed, undefined);
    advance(4999);
    assert.equal(socket.closed, undefined);
    advance(1);
    assert.equal(socket.closed, 4000);
    client.stop();
    advance(60000);
    assert.equal(sockets.length, 1);
});

test('freshness wakes at the inclusive stale boundary and handles future observations', () => {
    const store = new TelemetryStore();
    const now = Date.now();
    const frame = parseSnapshot(fixture(now));
    const reading = frame.sensors[0];
    reading.observedAt = new Date(now + 31000).toISOString();
    store.configuration = configuration(1000);
    store.update(frame);
    assert.equal(store.valid(reading, now), false);
    assert.equal(store.nextFreshnessChange, now + 1000);
    store.nextFreshnessChange = Infinity;
    assert.equal(store.valid(reading, now + 32000), true);
    assert.equal(store.nextFreshnessChange, now + 32001);
    store.nextFreshnessChange = Infinity;
    assert.equal(store.valid(reading, now + 32001), false);
    assert.equal(store.nextFreshnessChange, Infinity);
    store.clear();
    assert.equal(store.readingsFor(reading.identity.pluginId).length, 0);
    assert.equal(store.health(reading.identity.pluginId), undefined);
});
