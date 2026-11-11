import type { MetricData } from './MetricData.ts';
import type { PinData } from './PinData.ts';

export interface PanelData {
    pluginId: string;
    kind: 'pins' | 'status' | 'off';
    title: string;
    source: string;
    gpuUsage: string;
    coreVoltage: string;
    vramUsagePercent: number | null;
    state: string;
    tone: 'good' | 'warning' | 'error' | 'neutral';
    summary: MetricData[];
    rows: MetricData[];
    pins: PinData[];
    axis: string[];
    axisUnit: string;
    voltage: boolean;
    spread: string;
    highestPin: string;
    lowestPin: string;
    comparisonLabel: string;
    limitLabel: string;
    alerts: string[];
    identity: string;
    detail: string;
}