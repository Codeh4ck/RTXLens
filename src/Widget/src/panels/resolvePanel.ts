import { AsusId, MsiId, NvidiaId, WireViewId } from '../pluginIds.ts';
import { Settings } from '../settings/Settings.ts';
import { TelemetryStore } from '../telemetry/TelemetryStore.ts';
import type { PanelBinding } from './PanelBinding.ts';

export function resolvePanel(slot: number, settings: Settings, store: TelemetryStore, now: number): PanelBinding {
    const storedContent = settings.text(`panel${slot}`);
    const content = storedContent === 'asusStatus' ? 'nvidiaStatus' : storedContent;
    const nvidiaInstalled = store.catalog.some(plugin => plugin.id === NvidiaId)
        || store.health(NvidiaId) !== undefined;
    const automatic = content.startsWith('auto');
    const available = (id: string) => {
        if (content !== 'autoPins') {
            return store.reporting(id, now);
        }

        return store.readingsFor(id).some(
            (reading) =>
                ['GpuPin', 'PsuPin'].includes(reading.identity.component) &&
                ['Current', 'Voltage', 'Power'].includes(reading.identity.metric) &&
                store.valid(reading, now)
        );
    };

    const pluginId = automatic
        ? ((content === 'autoStatus' && nvidiaInstalled ? [NvidiaId] : [AsusId, WireViewId, MsiId]).find(available)
            ?? (content === 'autoStatus' && nvidiaInstalled ? NvidiaId : AsusId))
        : content === 'nvidiaStatus'
          ? nvidiaInstalled ? NvidiaId : AsusId
          : content.startsWith('wireview')
          ? WireViewId
          : content.startsWith('msi')
            ? MsiId
            : AsusId;

    return {
        kind: content === 'off' ? 'off' : /Status$/.test(content) ? 'status' : 'pins',
        pluginId,
        deviceId: store.device(pluginId, '', now),
        connectorId: pluginId === MsiId ? (content === 'msiConnector2' ? '2' : '1') : null,
        automatic
    };
}