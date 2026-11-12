import type { WarningNotice } from './WarningNotice.ts';

export const warningOptions = [
    ['none', 'Off'],
    ['overcurrent', 'Preview: pin overcurrent'],
    ['imbalance', 'Preview: current imbalance'],
    ['core', 'Preview: GPU core temperature'],
    ['memory', 'Preview: VRAM temperature'],
    ['wireview', 'Preview: WireView Pro II device faults'],
    ['offline', 'Preview: service offline'],
    ['stale', 'Preview: stale telemetry'],
    ['electrical', 'Preview: overcurrent + imbalance'],
    ['thermal', 'Preview: core + VRAM temperature'],
    ['combined', 'Preview: overcurrent + GPU temperature']
].map(([key, label]) => ({ key: key!, label: label! }));

export function previewWarnings(selection: string): WarningNotice[] {
    const warnings: Record<string, WarningNotice> = {
        overcurrent: {
            id: 'preview/overcurrent',
            title: 'Astral - Pin overcurrent',
            detail: 'P3: 10.4 A. Example protection limit: 9.2 A.',
            icon: 'overcurrent'
        },
        imbalance: {
            id: 'preview/imbalance',
            title: 'Astral - Current imbalance',
            detail: 'P2 High: 9.8 A / P5 Low: 2.1 A. Max spread: 7.7 A.',
            icon: 'imbalance'
        },
        core: {
            id: 'preview/core',
            title: 'GPU core temperature',
            detail: 'Example reading: 86 °C. The display warning threshold has been exceeded.',
            icon: 'vtemp'
        },
        memory: {
            id: 'preview/memory',
            title: 'VRAM temperature',
            detail: 'Example reading: 94 °C. The display warning threshold has been exceeded.',
            icon: 'vtemp'
        },
        wireview: {
            id: 'preview/wireview',
            title: 'WireView Pro II device faults',
            detail: 'Overtemperature, wire overcurrent and current imbalance reported by the device.',
            icon: 'overcurrent'
        },
        offline: {
            id: 'preview/offline',
            title: 'Service unavailable',
            detail: 'The widget is reconnecting. Check that the RTX Lens service is running.',
            icon: 'overcurrent'
        },
        stale: {
            id: 'preview/stale',
            title: 'Telemetry expired',
            detail: 'Readings are stale and are replaced with a dash until fresh data arrives.',
            icon: 'imbalance'
        }
    };

    const keys = (
        {
            electrical: ['overcurrent', 'imbalance'],
            thermal: ['core', 'memory'],
            combined: ['overcurrent', 'core']
        } as Record<string, string[]>
    )[selection] ?? [selection];

    return keys.flatMap((key) => (warnings[key] ? [warnings[key]!] : []));
}