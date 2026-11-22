import { hardwareLabels, msiModels } from './hardwareLabels.ts';
import { chartSettings } from '../settings/chartSettings.ts';
import { AsusId, MsiId, NvidiaId, WireViewId } from '../pluginIds.ts';
import { Settings } from '../settings/Settings.ts';
import { TelemetryStore } from '../telemetry/TelemetryStore.ts';
import { PeakStore } from '../telemetry/PeakStore.ts';
import { Formatter } from '../formatting/Formatter.ts';
import { faultDescription } from '../formatting/faultDescription.ts';
import type { PanelBinding } from './PanelBinding.ts';
import type { PanelData } from './PanelData.ts';
import type { MetricData } from './MetricData.ts';
import type { PinData } from './PinData.ts';

type ReadMetric = (component: string, metric: string, channel?: number | null, connector?: string | null) => number | null;

function metric(id: string, label: string, text: string, icon = 'vcore', warning = false): MetricData {
    return { id, label, text, icon, warning };
}

const wireTemperatures = [
    ['boardIn', 'Onboard in', 'MonitorBoard', 1],
    ['boardOut', 'Onboard out', 'MonitorBoard', 2],
    ['probe1', 'External probe 1', 'ExternalProbe', 1],
    ['probe2', 'External probe 2', 'ExternalProbe', 2]
] as const;

export class PanelPresenter {
    private store: TelemetryStore;
    private settings: Settings;
    private format: Formatter;
    private peaks: PeakStore;

    constructor(store: TelemetryStore, settings: Settings, format: Formatter, peaks: PeakStore) {
        this.store = store;
        this.settings = settings;
        this.format = format;
        this.peaks = peaks;
    }

    present(binding: PanelBinding, now: number, showSummary = true): PanelData {
        const { pluginId, deviceId, connectorId } = binding;
        const isPsu = pluginId === MsiId;
        const isWire = pluginId === WireViewId;
        let state = this.store.state(pluginId, now);

        if (
            state === 'Ready' &&
            deviceId &&
            !this.store.readingsFor(pluginId).some(
                (reading) =>
                    reading.identity.deviceId === deviceId && this.store.valid(reading, now)
            )
        ) {
            state = 'Unavailable';
        }

        const data: PanelData = {
            pluginId,
            kind: binding.kind,
            title: this.panelTitle(binding),
            source: (binding.automatic ? 'Auto · ' : '') + this.sourceName(pluginId, deviceId),
            state,
            gpuUsage: '',
            coreVoltage: '',
            vramUsagePercent: null,
            tone: state === 'Ready' ? 'good' : ['Error'].includes(state) ? 'error' : state === 'Disabled' ? 'neutral' : 'warning',
            summary: [],
            rows: [],
            pins: [],
            axis: [],
            axisUnit: 'A',
            voltage: false,
            spread: '-',
            highestPin: '-',
            lowestPin: '-',
            comparisonLabel: '',
            limitLabel: '',
            alerts: [],
            identity: `${pluginId}/${deviceId}/${connectorId}`,
            detail: this.store.snapshot?.plugins.find((plugin) => plugin.pluginId === pluginId)?.message ?? ''
        };

        const value = (component: string, metric: string, channel: number | null = null, connector: string | null = null) =>
            this.store.value(pluginId, deviceId, component, metric, now, channel, connector);

        if (isPsu) {
            this.populatePsu(data, binding, value, showSummary);
        } else {
            if (showSummary && binding.kind === 'status') data.summary = this.electricalSummary(binding, now);
            if (isWire) this.populateWireView(data, binding, value);
            else if (binding.kind === 'status') this.populateGpu(data, binding, value);
        }

        if (this.store.reporting(pluginId, now)) {
            for (const alert of this.store.snapshot?.alerts ?? []) {
                if (alert.pluginId !== pluginId || alert.deviceId !== deviceId) {
                    continue;
                }

                const pins = alert.pins.map((pin) => `P${pin.channel} ${this.format.value(pin.currentA, 'A')}`).join(', ');
                data.alerts.push(`${alert.kind === hardwareLabels.ImbalanceAlert ? 'Current imbalance' : 'Pin overcurrent'}: ${pins}`);
            }
        }

        if (data.alerts.length) {
            data.tone = 'error';
            data.state = 'Alert';
        }

        if (binding.kind === 'pins') {
            this.pins(binding, now, data);

            if (data.pins.every((pin) => pin.number === null) && state === 'Ready') {
                data.state = 'Pins unavailable';
                data.tone = 'warning';
                data.detail = 'Per-pin readings are unavailable. Other telemetry may still be available in a status panel.';
            }
        }

        return data;
    }

    private sourceName(pluginId: string, deviceId: string | null): string {
        switch (pluginId) {
            case MsiId:
                return Object.entries(msiModels).find(([product]) => deviceId?.toLowerCase().includes(product))?.[1]
                    ?? this.store.msiName;
            case WireViewId: return this.store.wireName || hardwareLabels.WireView;
            case NvidiaId: return this.store.health(pluginId)?.message || 'NVIDIA GeForce RTX';
            case AsusId: return hardwareLabels.Astral;
            default: return this.store.catalog.find(plugin => plugin.id === pluginId)?.name ?? pluginId;
        }
    }

    private panelTitle(binding: PanelBinding): string {
        if (binding.kind === 'pins') {
            return binding.pluginId === MsiId ? `PSU connector ${binding.connectorId}` : 'Pin distribution';
        }
        if (binding.pluginId === MsiId) return 'PSU status';
        return binding.pluginId === WireViewId ? 'Temperatures and faults' : 'GPU status';
    }

    private populatePsu(data: PanelData, binding: PanelBinding, value: ReadMetric, showSummary: boolean): void {
        if (!showSummary && binding.kind !== 'status') return;

        const temperature = metric('temperature', 'PSU temperature', this.format.temperature(value('Psu', 'Temperature')), 'vtemp');
        const fan = metric('fan', 'Fan speed', this.format.value(value('Psu', 'FanSpeed', 1), 'RPM', 0), 'vfan');
        const current = (connector: string) => this.format.value(value('PsuConnector', 'Current', null, connector), 'A');
        if (showSummary) {
            data.summary = [temperature, fan];
            if (binding.kind === 'pins') {
                data.summary.unshift(metric('connectorCurrent', 'Current', current(binding.connectorId === '2' ? '2' : '1'), 'pin'));
            }
        }
        if (binding.kind === 'status') {
            const automatic = value('Psu', 'FanAutomatic', 1);
            const mode = automatic === null ? '-' : automatic === 1 ? 'Automatic' : 'Manual';
            data.rows = [
                metric('connector1', 'Connector 1 current', current('1'), 'pin'),
                metric('connector2', 'Connector 2 current', current('2'), 'pin'),
                temperature, fan, metric('mode', 'Fan mode', mode, 'vfan'),
                metric('duty', 'Requested fan duty', this.format.value(value('Psu', 'FanDuty', 1), '%', 0), 'vfan')
            ];
        }
    }

    private electricalSummary({ pluginId, deviceId }: PanelBinding, now: number): MetricData[] {
        const isNvidia = pluginId === NvidiaId;
        const selectedSource = isNvidia
            ? this.store.configuration?.configuration.plugins[pluginId]?.options?.electricalSource
            : 'Automatic';
        const electricalPlugin = selectedSource === 'WireView' ? WireViewId : pluginId;
        const electricalDevice = electricalPlugin === pluginId ? deviceId : this.store.device(electricalPlugin, '', now);
        const electrical = (metric: string) => {
            const selection = isNvidia
                ? this.store.snapshot?.selections.find(selection => selection.name === `gpu-status/${metric.toLowerCase()}`)
                : undefined;
            const profiles = this.store.configuration?.configuration.monitoring?.connectorProfiles as { version?: number } | undefined;
            if (isNvidia && (profiles?.version === 1 || selection)) {
                return selection?.sensorKey ? this.store.byKey(selection.sensorKey, now)?.value ?? null : null;
            }

            return selectedSource === 'None' ? null
                : this.store.value(electricalPlugin, electricalDevice, 'GpuConnector', metric, now, 1);
        };
        const power = electrical('Power');
        const current = electrical('Current');
        const voltage = electrical('Voltage');
        return [
            metric('power', 'Power', this.format.value(power, 'W'), '12vhpwr'),
            metric('current', 'Total current', this.format.value(current, 'A'), 'pin'),
            metric('voltage', 'Voltage', this.format.value(voltage, 'V'), 'voltage')
        ];
    }

    private populateWireView(data: PanelData, binding: PanelBinding, value: ReadMetric): void {
        const faults = value('MonitorBoard', 'FaultStatus');
        if (binding.kind === 'status') {
            data.rows = [
                ...wireTemperatures.map(([id, label, component, channel]) =>
                    metric(id, label, this.format.temperature(value(component, 'Temperature', channel)), 'vtemp')),
                metric(
                    'capability',
                    'Cable capability',
                    this.format.value(value('GpuConnector', 'PowerCapability', 1), 'W', 0),
                    '12vhpwr'
                ),
                metric(
                    'faults',
                    'Active faults',
                    faultDescription(faults),
                    'overcurrent',
                    (faults ?? 0) > 0
                ),
                metric('faultLog', 'Fault log', faultDescription(value('MonitorBoard', 'FaultLog')), 'imbalance')
            ];
        }

        if ((faults ?? 0) > 0) {
            data.alerts.push(`WireView Pro II: ${faultDescription(faults)}`);
        }
    }

    private populateGpu(data: PanelData, { pluginId, deviceId }: PanelBinding, value: ReadMetric): void {
        data.gpuUsage = this.format.value(value('GpuCore', 'Utilization'), '%', 0);
        const coreVoltage = value('GpuCore', 'Voltage');
        data.coreVoltage = this.format.value(coreVoltage === null ? null : coreVoltage * 1000, 'mV');

        const coreTemperature = value('GpuCore', 'Temperature');
        const memoryTemperature = value('GpuMemory', 'Temperature');
        const used = value('GpuMemory', 'MemoryUsed');
        const total = value('GpuMemory', 'MemoryTotal');
        const utilization = value('GpuMemory', 'Utilization');

        if (used !== null && total !== null && total > 0) {
            data.vramUsagePercent = Math.max(0, Math.min(100, utilization ?? used / total * 100));
        }

        const temperatureWarnings = this.store.snapshot?.warnings?.states.filter(state =>
            state.pluginId === pluginId && state.deviceId === deviceId && state.phase === 'Active' && state.icon === 'temperature'
        ) ?? [];
        const warns = (sensor: string) => temperatureWarnings.some(state => state.typeId === `${pluginId}/${sensor}-temperature`);
        data.rows = [
            metric('coreClock', 'Core clock', this.format.value(value('GpuCore', 'Clock'), 'MHz', 0), 'vcore'),
            metric('memoryClock', 'Memory clock', this.format.value(value('GpuMemory', 'Clock'), 'MHz', 0), 'vram'),
            metric('vram', 'VRAM usage', this.format.memory(used, total, utilization), 'vramusage'),
            metric('temperatureCore', 'Core temp', this.format.temperature(coreTemperature), 'vtemp', warns('core')),
            metric('temperatureMemory', 'VRAM temp', this.format.temperature(memoryTemperature), 'vtemp', warns('memory'))
        ];

        const hideFans = this.settings.enabled('hideGpuFans');
        for (const [option, component, label] of [
            ['showCrossbarClock', 'GpuCrossbar', 'Crossbar'],
            ['showSystemClock', 'GpuSystem', 'System']
        ] as const) {
            if ((this.settings.stackGpuClocks || this.settings.enabled(option)) && this.store.reading(pluginId, deviceId, component, 'Clock')) {
                data.rows.push(metric(component, label, this.format.value(value(component, 'Clock'), 'MHz', 0), 'vcore'));
            }
        }

        if (!hideFans) {
            const channels = this.store.snapshot?.sensors.filter(reading => reading.identity.pluginId === pluginId
                && reading.identity.deviceId === deviceId && reading.identity.component === 'GpuFan'
                && reading.identity.metric === 'FanSpeed' && reading.identity.channel !== null)
                .map(reading => reading.identity.channel!).sort((left, right) => left - right).slice(0, 2) ?? [];
            const fans = channels.map((channel, index) => {
                const rpm = this.format.value(value('GpuFan', 'FanSpeed', channel), 'RPM', 0);
                const duty = value('GpuFan', 'FanDuty', channel);
                const text = this.settings.enabled('showFanDuty') && duty !== null
                    ? `${rpm} (${this.format.value(duty, '%', 0)})`
                    : rpm;
                return metric(`fan${index + 1}`, `Fan ${index + 1}`, text, 'vfan');
            });
            data.rows.push(...(fans.length ? fans : [metric('fan1', 'Fans', '-', 'vfan')]));
        }

        if (temperatureWarnings.length) {
            data.alerts.push('Display warning: GPU temperature threshold exceeded');
        }
    }

    private pins(binding: PanelBinding, now: number, data: PanelData): void {
        const isPsu = binding.pluginId === MsiId;
        const isWire = binding.pluginId === WireViewId;
        const prefix = isWire ? 'wireview' : 'asus';
        const unit = isPsu ? 'A' : this.settings.text(`${prefix}Metric`);
        const metric = unit === 'W' ? 'Power' : unit === 'V' ? 'Voltage' : 'Current';
        const active = this.store.snapshot?.settingsProfile;
        const associatedDevice = isWire ? active?.wireViewDeviceId : active?.gpuDeviceId;
        const profile = !isPsu && binding.deviceId && binding.deviceId === associatedDevice ? active?.profile : undefined;
        const publishedLimits = this.store.snapshot?.pinWarningLimits;
        const profileLimit = publishedLimits
            ? publishedLimits.find(item => item.pluginId === binding.pluginId && item.deviceId === binding.deviceId)?.overcurrentAmperes
            : !isWire || profile?.useWireViewOvercurrentFallback ? profile?.overcurrentAmperes : undefined;
        const deviceLimit = isWire || !publishedLimits ? this.store.limit(binding.pluginId, binding.deviceId) : null;
        const limit = isPsu ? null
            : profileLimit === undefined ? deviceLimit
            : deviceLimit === null ? profileLimit : Math.min(profileLimit, deviceLimit);
        const cable = this.store.value(binding.pluginId, binding.deviceId, 'GpuConnector', 'PowerCapability', now, 1);
        const profileFallback = this.settings.enabled('overridePowerFallback') ? undefined : profile?.powerFallbackPerPin;
        const profileCurrent = this.settings.enabled('overrideCurrentMax') ? undefined : profile?.currentCeiling;
        const powerCeiling = profile?.powerCeilingPerPin
            ?? (cable !== null && cable > 0 ? cable / 6 : profileFallback ?? this.settings.number('powerCableFallback') / 6);
        const max = unit === 'V' ? profile?.voltageMaximum ?? 12.6
            : unit === 'W' ? powerCeiling : profileCurrent ?? this.settings.number('currentMax');
        const min = unit === 'V' ? profile?.voltageMinimum ?? 11.4 : 0;

        if (profile) {
            data.source += ' · ' + profile.name;
        }
        const fill = (value: number) => Math.max(0, Math.min(100, ((value - min) / (max - min)) * 100));
        const component = isPsu ? 'PsuPin' : 'GpuPin';
        const psuDevice = this.store.device(MsiId, '', now);
        const comparison = !isPsu && this.settings.enabled(`${prefix}Compare`);
        const connector = this.settings.text(`${prefix}Connector`);
        data.axisUnit = unit;
        data.voltage = unit === 'V';

        if (this.settings.enabled(chartSettings.Axis)) {
            const ticks = [1, 0.75, 0.5, 0.25, 0].map(fraction => min + (max - min) * fraction);
            data.axis = ticks.map((tick) =>
                this.format.value(tick, unit, 1)
            );
        }

        data.limitLabel = isPsu
            ? 'PSU-side current'
            : limit === null
              ? 'Protection limit unavailable'
              : `Headroom to ${this.format.value(limit, 'A')} ${limit === profileLimit ? 'profile' : isWire ? 'device' : 'service'} limit`;
        data.comparisonLabel = comparison
            ? `PSU C${connector} · current beneath P1-P6` +
              (!isWire ? ' · reversed P6-P1' : '') +
              (!this.store.reporting(MsiId, now) || !psuDevice ? ' · unavailable' : '')
            : '';

        if (profile) {
            data.detail = profile.name + ' settings profile';
        } else if (unit === 'W' && !(cable !== null && cable > 0)) {
            data.detail = `Display range uses ${this.format.value(this.settings.number('powerCableFallback'), 'W', 0)} cable equivalent; capability unknown`;
        }

        data.pins = Array.from({ length: 6 }, (_, index): PinData => {
            const channel = index + 1;
            const comparisonChannel = isWire ? channel : 7 - channel;
            const current = this.store.value(binding.pluginId, binding.deviceId, component, 'Current', now, channel, binding.connectorId);
            const value = this.store.value(binding.pluginId, binding.deviceId, component, metric, now, channel, binding.connectorId);
            const headroom = this.settings.enabled(chartSettings.Headroom) && limit !== null && current !== null ? limit - current : null;
            const peak = this.settings.enabled(chartSettings.Peak) ? this.peaks.observe(`${data.identity}/${channel}/${metric}`, value) : null;
            const warning =
                this.store.value(
                    binding.pluginId,
                    binding.deviceId,
                    component,
                    'OvercurrentDetected',
                    now,
                    channel,
                    binding.connectorId
                ) === 1 || (isWire && this.store.snapshot?.alerts.some(alert =>
                    alert.pluginId === binding.pluginId && alert.deviceId === binding.deviceId
                    && alert.kind === hardwareLabels.ProfileOvercurrentAlert
                    && alert.pins.some(pin => pin.channel === channel)) === true);

            return {
                value: this.format.value(value, unit),
                number: value,
                current,
                headroom: headroom === null ? '-' : `${headroom >= 0 ? '+' : ''}${this.format.value(headroom, 'A')}`,
                headroomValue: headroom,
                comparison: comparison
                    ? this.format.value(this.store.value(MsiId, psuDevice, 'PsuPin', 'Current', now, comparisonChannel, connector), 'A')
                    : '',
                fill: value === null ? 0 : fill(value),
                peak: peak === null ? null : fill(peak),
                warning,
                overflow: value !== null && (value < min || value > max)
            };
        });

        if (!this.settings.enabled(chartSettings.Spread) || isPsu) {
            return;
        }

        const currents = data.pins.map((pin, index) => ({ current: pin.current, index })).filter((pin) => pin.current !== null);

        if (currents.length >= 2) {
            currents.sort((a, b) => a.current! - b.current!);

            const low = currents[0]!;
            const high = currents[currents.length - 1]!;
            data.spread = this.format.value(high.current! - low.current!, 'A');
            data.highestPin = `P${high.index + 1}`;
            data.lowestPin = `P${low.index + 1}`;
        }
    }
}