import test from 'node:test';
import assert from 'node:assert/strict';
import { ServiceClient } from '../src/transport/ServiceClient.ts';
import { TelemetryStore } from '../src/telemetry/TelemetryStore.ts';
import { fixture, configuration, catalog } from './fixtures.mjs';

test('failed WireView config reads respect the cooldown, device and revision', async (t) => {
    let socket;
    class FakeSocket {
        constructor() { socket = this; }
        close() {}
    }
    const originalSocket = globalThis.WebSocket;
    globalThis.WebSocket = FakeSocket;
    t.after(() => { globalThis.WebSocket = originalSocket; });
    let now = Date.now();
    t.mock.method(Date, 'now', () => now);
    let reads = 0;
    let available = false;
    t.mock.method(globalThis, 'fetch', async (url) => {
        const { pathname } = new URL(url);
        if (pathname.endsWith('/device/config')) {
            reads++;
            return { ok: available, status: available ? 200 : 503, json: async () => ({ wireOcpFaultThresholdA: 9.2 }) };
        }
        return { ok: true, json: async () => pathname === '/plugins' ? catalog() : configuration() };
    });

    const store = new TelemetryStore();
    const client = new ServiceClient(store, () => {});
    t.after(() => client.stop());
    client.start(59273);
    let sequence = 0;
    async function send(device = 'com3', revision = 7) {
        const snapshot = fixture(now);
        snapshot.sequence = ++sequence;
        snapshot.configurationRevision = revision;
        for (const sensor of snapshot.sensors) {
            if (sensor.identity.pluginId === 'wireview-pro2') sensor.identity.deviceId = device;
        }
        socket.onmessage({ data: JSON.stringify(snapshot) });
        await new Promise(resolve => setImmediate(resolve));
    }
    for (let i = 0; i < 10; i++) await send();
    assert.equal(reads, 1);
    assert.equal(store.wireLimit, null);
    await send('com4');
    assert.equal(reads, 2);
    await send('com4', 8);
    await send('com4', 8);
    assert.equal(reads, 3);
    now += 30000;
    available = true;
    await send('com4', 8);
    assert.equal(reads, 4);
    assert.equal(store.wireLimitDevice, 'com4');
    assert.equal(store.wireLimit, 9.2);
});
