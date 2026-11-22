import { fixture as legacyFixture, configuration as legacyConfiguration, catalog as legacyCatalog } from './fixtures.mjs';

export const nvidiaId = 'nvidia-pci/01/00/2b8510de/89e31043';

export function fixture(now = Date.now()) {
    const frame = legacyFixture(now);
    const generic = frame.sensors.filter(reading => reading.identity.pluginId === 'asus-astral'
        && !['GpuPin', 'GpuBoard'].includes(reading.identity.component)).map(reading => ({
            ...reading,
            identity: { ...reading.identity, pluginId: 'generic-nvidia', deviceId: nvidiaId }
        }));

    const add = (component, metric, unit, value, channel = null) => generic.push({
        identity: { pluginId: 'generic-nvidia', deviceId: nvidiaId, component, metric, channel, connectorId: null },
        value, unit, observedAt: frame.timestamp, quality: 'Valid'
    });

    add('GpuCrossbar', 'Clock', 'Megahertz', 2175);
    add('GpuSystem', 'Clock', 'Megahertz', 1650);
    add('GpuCore', 'Voltage', 'Volts', 0.925);
    add('GpuHotspot', 'Temperature', 'Celsius', 73.28);
    add('GpuFan', 'FanDuty', 'Percent', 50, 1);
    add('GpuFan', 'FanDuty', 'Percent', 45, 2);
    add('GpuConnector', 'Voltage', 'Volts', 12.005, 1);
    frame.sensors.push(...generic);
    frame.plugins.push({ pluginId: 'generic-nvidia', state: 'Ready', lastSuccessAt: frame.timestamp, message: 'NVIDIA GeForce RTX 5090' });
    return frame;
}

export function configuration(staleAfterMs = 5000) {
    const result = legacyConfiguration(staleAfterMs);
    result.configuration.plugins['generic-nvidia'] = {
        enabled: true, pollIntervalMs: 1000, staleAfterMs,
        options: { coreTemperatureWarningC: 81, memoryTemperatureWarningC: 85, electricalSource: 'Automatic' }
    };
    return result;
}

export function catalog() {
    const result = legacyCatalog();
    result.plugins.push({ id: 'generic-nvidia', name: 'GenericNvidia', enabled: true, state: 'Ready',
        lastSuccessAt: new Date().toISOString(), message: 'NVIDIA GeForce RTX 5090', capabilities: [] });
    return result;
}
