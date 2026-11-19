import test from 'node:test';
import assert from 'node:assert/strict';
import { ServiceClient } from '../src/transport/ServiceClient.ts';
import { TelemetryStore } from '../src/telemetry/TelemetryStore.ts';
import { catalog, configuration } from './fixtures.mjs';

test('widget discovery loads the catalog and configuration without polling health', async (t) => {
    const paths = [];
    const expectedCatalog = catalog();
    const originalSocket = globalThis.WebSocket;
    globalThis.WebSocket = class { close() {} };
    t.after(() => { globalThis.WebSocket = originalSocket; });
    t.mock.method(globalThis, 'fetch', async (url) => {
        const { pathname } = new URL(url);
        paths.push(pathname);
        return { ok: true, json: async () => pathname === '/plugins' ? expectedCatalog : configuration() };
    });

    const store = new TelemetryStore();
    const client = new ServiceClient(store, () => {});
    t.after(() => client.stop());
    client.start(59273);
    await new Promise(resolve => setImmediate(resolve));
    assert.deepEqual(store.catalog, expectedCatalog.plugins);
    assert.equal(store.configuration.revision, configuration().revision);
    assert.deepEqual(paths.sort(), ['/configuration', '/plugins']);
});
