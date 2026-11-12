import { Settings } from '../settings/Settings.ts';
import { TelemetryStore } from '../telemetry/TelemetryStore.ts';
import { PeakStore } from '../telemetry/PeakStore.ts';
import { Formatter } from '../formatting/Formatter.ts';
import { faultDescription } from '../formatting/faultDescription.ts';
import type { PanelBinding } from './PanelBinding.ts';
import type { PanelData } from './PanelData.ts';
import type { MetricData } from './MetricData.ts';
import type { PinData } from './PinData.ts';

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
        const isPsu = pluginId === 'msi-psu';
        const isWire = pluginId === 'wireview-pro2';
        const isNvidia = pluginId === 'generic-nvidia';
        const source = isPsu
            ? /pid_808c/i.test(deviceId ?? '')
                ? 'MSI MPG Ai1600TS'
                : /pid_aa6f/i.test(deviceId ?? '')
                  ? 'MSI MPG Ai1300TS'
                  : this.store.msiName
            : isWire
              ? this.store.wireName || 'WireView Pro II'
              : isNvidia
                ? this.store.snapshot?.plugins.find(plugin => plugin.pluginId === pluginId)?.message || 'NVIDIA GeForce RTX'
                : 'ROG Astral';

        let state = this.store.state(pluginId, now);

        if (
            state === 'Ready' &&
            deviceId &&
            !this.store.snapshot?.sensors.some(
                (reading) =>
                    reading.identity.pluginId === pluginId && reading.identity.deviceId === deviceId && this.store.valid(reading, now)
            )
        ) {
            state = 'Unavailable';
        }

        const data: PanelData = {
            pluginId,
            kind: binding.kind,
            title:
                binding.kind === 'pins'
                    ? isPsu
                        ? `PSU connector ${connectorId}`
                        : 'Pin distribution'
                    : isPsu
                      ? 'PSU status'
                      : isWire
                        ? 'Temperatures and faults'
                        : 'GPU status',
            source: (binding.automatic ? 'Auto · ' : '') + source,
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

        const metric = (id: string, label: string, text: string, icon = 'vcore', warning = false): MetricData => ({
            id,
            label,
            text,
            icon,
            warning
        });

        if (isPsu) {
            const current1 = value('PsuConnector', 'Current', null, '1');
            const current2 = value('PsuConnector', 'Current', null, '2');
            const automatic = value('Psu', 'FanAutomatic', 1);
            const fanMode = automatic === null ? '-' : automatic === 1 ? 'Automatic' : 'Manual';

            if (showSummary) {
                data.summary =
                    binding.kind === 'pins'
                        ? [
                              metric(
                                  'connectorCurrent',
                                  'Current',
                                  this.format.value(connectorId === '2' ? current2 : current1, 'A'),
                                  'pin'
                              ),
                              metric('temperature', 'PSU temperature', this.format.temperature(value('Psu', 'Temperature')), 'vtemp'),
                              metric('fan', 'Fan speed', this.format.value(value('Psu', 'FanSpeed', 1), 'RPM', 0), 'vfan')
                          ]
                        : [
                              metric('temperature', 'PSU temperature', this.format.temperature(value('Psu', 'Temperature')), 'vtemp'),
                              metric('fan', 'Fan speed', this.format.value(value('Psu', 'FanSpeed', 1), 'RPM', 0), 'vfan')
                          ];
            }

            if (binding.kind === 'status') {
                data.rows = [
                    metric('connector1', 'Connector 1 current', this.format.value(current1, 'A'), 'pin'),
                    metric('connector2', 'Connector 2 current', this.format.value(current2, 'A'), 'pin'),
                    metric('temperature', 'PSU temperature', this.format.temperature(value('Psu', 'Temperature')), 'vtemp'),
                    metric('fan', 'Fan speed', this.format.value(value('Psu', 'FanSpeed', 1), 'RPM', 0), 'vfan'),
                    metric('mode', 'Fan mode', fanMode, 'vfan'),
                    metric('duty', 'Requested fan duty', this.format.value(value('Psu', 'FanDuty', 1), '%', 0), 'vfan')
                ];
            }
        } else {
            if (showSummary && binding.kind === 'status') {
                const selectedSource = isNvidia
                    ? this.store.configuration?.configuration.plugins[pluginId]?.options?.electricalSource
                    : 'Automatic';
                const electricalPlugin = selectedSource === 'WireView' ? 'wireview-pro2' : pluginId;
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
                data.summary = [
                    metric('power', 'Power', this.format.value(power, 'W'), '12vhpwr'),
                    metric('current', 'Total current', this.format.value(current, 'A'), 'pin'),
                    metric('voltage', 'Voltage', this.format.value(voltage, 'V'), 'voltage')
                ];
            }

            if (isWire) {
                if (binding.kind === 'status') {
                    data.rows = [
                        metric('boardIn', 'Onboard in', this.format.temperature(value('MonitorBoard', 'Temperature', 1)), 'vtemp'),
                        metric('boardOut', 'Onboard out', this.format.temperature(value('MonitorBoard', 'Temperature', 2)), 'vtemp'),
                        metric('probe1', 'External probe 1', this.format.temperature(value('ExternalProbe', 'Temperature', 1)), 'vtemp'),
                        metric('probe2', 'External probe 2', this.format.temperature(value('ExternalProbe', 'Temperature', 2)), 'vtemp'),
                        metric(
                            'capability',
                            'Cable capability',
                            this.format.value(value('GpuConnector', 'PowerCapability', 1), 'W', 0),
                            '12vhpwr'
                        ),
                        metric(
                            'faults',
                            'Active faults',
                            faultDescription(value('MonitorBoard', 'FaultStatus')),
                            'overcurrent',
                            (value('MonitorBoard', 'FaultStatus') ?? 0) > 0
                        ),
                        metric('faultLog', 'Fault log', faultDescription(value('MonitorBoard', 'FaultLog')), 'imbalance')
                    ];
                }

                if ((value('MonitorBoard', 'FaultStatus') ?? 0) > 0) {
                    data.alerts.push(`WireView Pro II: ${faultDescription(value('MonitorBoard', 'FaultStatus'))}`);
                }
            } else if (binding.kind === 'status') {
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

                data.rows.push(...(fans.length ? fans : [metric('fan1', 'Fans', '-', 'vfan')]));

                if (temperatureWarnings.length) {
                    data.alerts.push('Display warning: GPU temperature threshold exceeded');
                }
            }
        }

        if (this.store.reporting(pluginId, now)) {
            for (const alert of this.store.snapshot?.alerts ?? []) {
                if (alert.pluginId !== pluginId || alert.deviceId !== deviceId) {
                    continue;
                }

                const pins = alert.pins.map((pin) => `P${pin.channel} ${this.format.value(pin.currentA, 'A')}`).join(', ');
                data.alerts.push(`${alert.kind === 'CurrentImbalance' ? 'Current imbalance' : 'Pin overcurrent'}: ${pins}`);
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

    private pins(binding: PanelBinding, now: number, data: PanelData): void {
        const isPsu = binding.pluginId === 'msi-psu';
        const isWire = binding.pluginId === 'wireview-pro2';
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
        const powerCeiling = profile?.powerCeilingPerPin
            ?? (cable !== null && cable > 0 ? cable / 6 : profile?.powerFallbackPerPin ?? this.settings.number('powerCableFallback') / 6);
        const max = unit === 'V' ? profile?.voltageMaximum ?? 12.6
            : unit === 'W' ? powerCeiling : profile?.currentCeiling ?? this.settings.number('currentMax');
        const min = unit === 'V' ? profile?.voltageMinimum ?? 11.4 : 0;

        if (profile) {
            data.source += ' · ' + profile.name;
        }
        const fill = (value: number) => Math.max(0, Math.min(100, ((value - min) / (max - min)) * 100));
        const component = isPsu ? 'PsuPin' : 'GpuPin';
        const psuDevice = this.store.device('msi-psu', '', now);
        const comparison = !isPsu && this.settings.enabled(`${prefix}Compare`);
        const connector = this.settings.text(`${prefix}Connector`);
        data.axisUnit = unit;
        data.voltage = unit === 'V';

        if (this.settings.enabled('showAxis')) {
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
              (!this.store.reporting('msi-psu', now) || !psuDevice ? ' · unavailable' : '')
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
            const headroom = this.settings.enabled('showHeadroom') && limit !== null && current !== null ? limit - current : null;
            const peak = this.settings.enabled('showPeak') ? this.peaks.observe(`${data.identity}/${channel}/${metric}`, value) : null;
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
                    && alert.kind === 'wireview-pro2/profile-pin-overcurrent'
                    && alert.pins.some(pin => pin.channel === channel)) === true);

            return {
                value: this.format.value(value, unit),
                number: value,
                current,
                headroom: headroom === null ? '-' : `${headroom >= 0 ? '+' : ''}${this.format.value(headroom, 'A')}`,
                headroomValue: headroom,
                comparison: comparison
                    ? this.format.value(this.store.value('msi-psu', psuDevice, 'PsuPin', 'Current', now, comparisonChannel, connector), 'A')
                    : '',
                fill: value === null ? 0 : fill(value),
                peak: peak === null ? null : fill(peak),
                warning,
                overflow: value !== null && (value < min || value > max)
            };
        });

        if (!this.settings.enabled('showSpread') || isPsu) {
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