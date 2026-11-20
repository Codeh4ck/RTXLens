import { serviceRoutes } from './serviceRoutes.ts';
const paths = new Set<string>([serviceRoutes.Health, serviceRoutes.Plugins, serviceRoutes.Configuration, serviceRoutes.WireViewConfiguration]);

export class ReadOnlyApi {
    readonly baseUrl: string;

    constructor(port: number) {
        if (!Number.isInteger(port) || port < 1 || port > 65535) {
            throw new Error('Invalid local service port.');
        }

        this.baseUrl = `http://localhost:${port}`;
    }

    async get(path: string, signal: AbortSignal): Promise<unknown> {
        if (!paths.has(path)) {
            throw new Error('This endpoint is not allowed in the read-only widget.');
        }

        const response = await fetch(this.baseUrl + path, {
            method: 'GET',
            credentials: 'omit',
            cache: 'no-store',
            redirect: 'error',
            signal: AbortSignal.any([signal, AbortSignal.timeout(4000)])
        });

        if (!response.ok) {
            throw new Error(`Service returned HTTP ${response.status}.`);
        }

        return response.json() as Promise<unknown>;
    }

    socketUrl(): string {
        return this.baseUrl.replace('http:', 'ws:') + serviceRoutes.Sensors;
    }
}