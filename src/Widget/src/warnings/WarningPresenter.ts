import { TelemetryStore } from '../telemetry/TelemetryStore.ts';
import type { WarningNotice } from './WarningNotice.ts';

export class WarningPresenter {
    private store: TelemetryStore;

    constructor(store: TelemetryStore) {
        this.store = store;
    }

    present(now: number): WarningNotice[] {
        const notices: WarningNotice[] = [];

        if (['offline', 'reconnecting', 'incompatible'].includes(this.store.connection)) {
            notices.push({
                id: 'connection',
                title: 'Service unavailable',
                detail: this.store.connectionMessage,
                icon: 'overcurrent'
            });
        }

        for (const plugin of this.store.snapshot?.plugins ?? []) {
            const state = this.store.state(plugin.pluginId, now);

            if (state === 'Stale') {
                notices.push({
                    id: `stale/${plugin.pluginId}`,
                    title: `${this.name(plugin.pluginId)} - Telemetry expired`,
                    detail: 'Waiting for fresh readings. Expired values are displayed as a dash.',
                    icon: 'imbalance'
                });
            }
        }

        if (this.store.snapshot && !this.store.snapshot.warnings) {
            notices.push({
                id: 'warnings/unsupported',
                title: 'Warning service update required',
                detail: 'Update the backend service to use shared warning settings and reminders.',
                icon: 'imbalance'
            });
        }

        for (const state of this.store.snapshot?.warnings?.states ?? []) {
            const current = this.store.reporting(state.pluginId, now);
            const phase =
                !current || state.phase === 'Unavailable'
                    ? ' - Data unavailable'
                    : state.phase === 'Recovering'
                      ? ' - Confirming recovery'
                      : '';
            notices.push({
                id: state.id,
                title: state.title + phase,
                detail: state.detail,
                icon:
                    (
                        {
                            temperature: 'vtemp',
                            imbalance: 'imbalance',
                            voltage: '12vhpwr',
                            power: '12vhpwr'
                        } as Record<string, string>
                    )[state.icon] ?? 'overcurrent'
            });
        }

        return notices;
    }

    private name(pluginId: string): string {
        return (
            (
                {
                    'asus-astral': 'Astral',
                    'wireview-pro2': 'WireView Pro II',
                    'msi-psu': 'MSI PSU'
                } as Record<string, string>
            )[pluginId] ?? pluginId
        );
    }
}