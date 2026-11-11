import { Settings } from '../settings/Settings.ts';
import type { PanelData } from '../panels/PanelData.ts';
import type { MetricData } from '../panels/MetricData.ts';
import { MetricText } from './MetricText.ts';
import { setText, setVariable } from './setText.ts';

const template = `
    <header class="panel-header">
        <div class="panel-heading"><span class="icon panel-icon" data-icon="pin" aria-hidden="true"></span>
            <div class="heading-text"><h1 class="panel-title"></h1><p class="panel-source"></p></div></div>
        <div class="panel-actions"><span class="gpu-usage" aria-label="GPU usage"><span>GPU</span><b class="gpu-usage-value"></b></span><span class="core-voltage" aria-label="GPU core voltage">C. VLT <b class="core-voltage-value"></b></span>
            <span class="status-pill" role="status"></span>
            <nav class="page-controls" aria-label="Hidden panels"><button type="button" class="previous-panel" aria-label="Previous panel">‹</button><button type="button" class="next-panel" aria-label="Next panel">›</button></nav></div>
    </header>
    <div class="panel-summary"></div>
    <div class="chart-caption">
        <div class="spread-badge" aria-label="Current spread and pin extremes" title="Maximum current minus minimum current">
            <div class="spread-reading"><b class="spread-value"></b><span class="spread-label">Max Spread</span></div>
            <div class="spread-extreme"><b class="highest-pin"></b><span class="extreme-label">High</span></div>
            <div class="spread-extreme"><b class="lowest-pin"></b><span class="extreme-label">Low</span></div></div>
        <div class="panel-tools"><div class="metric-switch" aria-label="Pin metric">
            <button type="button" data-metric="A" aria-pressed="true">A</button>
            <button type="button" data-metric="W" aria-pressed="false">W</button>
            <button type="button" data-metric="V" aria-pressed="false">V</button></div>
            <button type="button" class="reset-peak" aria-label="Reset peak hold" title="Reset peak hold">↺</button></div>
    </div>
    <div class="pin-chart" role="img" aria-label="Six pin measurements">
        <div class="chart-axis"></div><div class="chart-pins"></div>
    </div>
    <div class="status-rows"></div>
    <p class="panel-detail"></p>
    <div class="panel-alert" role="status"></div>`;

const gpuStatusTemplate = `
    <section class="gpu-card gpu-clocks" aria-label="Clocks">
        <h2 class="gpu-card-title"><span class="icon" data-icon="vcore" aria-hidden="true"></span>Clocks</h2>
        <div class="gpu-reading-columns"></div>
    </section>
    <section class="gpu-card gpu-vram" aria-label="VRAM usage">
        <h2 class="gpu-card-title"><span class="icon" data-icon="vramusage" aria-hidden="true"></span>VRAM usage</h2>
        <div class="gpu-vram-reading">
            <div class="gpu-vram-track" role="progressbar" aria-label="VRAM usage" aria-valuemin="0" aria-valuemax="100" hidden>
                <span class="gpu-vram-fill"></span>
            </div>
        </div>
    </section>
    <section class="gpu-card gpu-temperatures" aria-label="Temperatures">
        <h2 class="gpu-card-title"><span class="icon" data-icon="vtemp" aria-hidden="true"></span>Temperatures</h2>
        <div class="gpu-reading-columns"></div>
    </section>
    <section class="gpu-card gpu-fans" aria-label="Fans">
        <h2 class="gpu-card-title"><span class="icon" data-icon="vfan" aria-hidden="true"></span>Fans</h2>
        <div class="gpu-reading-columns"></div>
    </section>`;

export class PanelView {
    readonly element: HTMLElement;
    private slot: number;
    private summary: {
        element: HTMLElement;
        label: HTMLElement;
        text: MetricText;
        icon: HTMLElement;
    }[] = [];
    private rows: {
        element: HTMLElement;
        label: HTMLElement | null;
        text: MetricText;
        icon: HTMLElement | null;
    }[] = [];
    private pins: {
        element: HTMLElement;
        text: MetricText;
        comparison: MetricText | null;
        headroom: MetricText | null;
        fill: HTMLElement;
        peak: HTMLElement | null;
    }[] = [];
    private nodes: Record<string, HTMLElement> = {};
    private tickNodes: HTMLElement[] = [];
    private signature = '';
    private settings: Settings;
    private usage: MetricText | null;
    private coreVoltage: MetricText | null;
    private vramTrack: HTMLElement | null = null;
    private spread: MetricText | null;

    constructor(
        element: HTMLElement,
        slot: number,
        settings: Settings,
        data: PanelData,
        reset: () => void,
        metricChanged: (slot: number, metric: string) => void,
        pageChanged: (direction: number) => void
    ) {
        this.element = element;
        this.slot = slot;
        this.settings = settings;
        element.innerHTML = template;

        const pins = data.kind === 'pins';
        const psu = data.pluginId === 'msi-psu';
        const gpuStatus = data.kind === 'status' && ['asus-astral', 'generic-nvidia'].includes(data.pluginId);
        element.dataset.gpuStatus = String(gpuStatus);

        if (pins || !['asus-astral', 'generic-nvidia'].includes(data.pluginId)) {
            element.querySelector('.gpu-usage')!.remove();
            element.querySelector('.core-voltage')!.remove();
        }

        if (!settings.enabled('showSource')) {
            element.querySelector('.panel-source')!.remove();
        }

        if (!settings.enabled(`panel${slot}Hero`) || !data.summary.length) {
            element.querySelector('.panel-summary')!.remove();
        }

        if (!pins) {
            element.querySelector('.chart-caption')!.remove();
            element.querySelector('.pin-chart')!.remove();
        } else {
            element.querySelector('.status-rows')!.remove();

            if (!settings.enabled('showAxis')) {
                element.querySelector('.chart-axis')!.remove();
            }

            if (psu || !settings.enabled('showSpread')) {
                element.querySelector('.spread-badge')!.remove();
            }

            if (!settings.enabled('showPeak')) {
                element.querySelector('.reset-peak')!.remove();
            }

            if (psu) {
                element.querySelector('.metric-switch')!.remove();
            }

            if (psu && !settings.enabled('showPeak')) {
                element.querySelector('.chart-caption')!.remove();
            }
        }

        // Per-panel alerts are represented once by the shared banner/flyout.
        element.querySelector('.panel-alert')!.remove();

        for (const name of [
            'panel-title',
            'panel-source',
            'gpu-usage',
            'status-pill',
            'chart-caption',
            'spread-value',
            'highest-pin',
            'lowest-pin',
            'pin-chart',
            'status-rows',
            'panel-detail',
            'panel-tools',
            'metric-switch',
            'reset-peak'
        ]) {
            const node = element.querySelector<HTMLElement>(`.${name}`);

            if (node) {
                this.nodes[name] = node;
            }
        }

        this.usage = element.querySelector('.gpu-usage-value') ? new MetricText(element.querySelector('.gpu-usage-value')!) : null;
        this.coreVoltage = element.querySelector('.core-voltage-value') ? new MetricText(element.querySelector('.core-voltage-value')!) : null;
        this.spread = this.nodes['spread-value'] ? new MetricText(this.nodes['spread-value']) : null;

        for (let index = 0; index < (pins && settings.enabled('showAxis') ? 5 : 0); index++) {
            const tick = document.createElement('span');
            element.querySelector('.chart-axis')!.append(tick);
            this.tickNodes.push(tick);
        }

        for (let index = 0; index < data.summary.length; index++) {
            const cell = document.createElement('div');
            cell.className = 'summary-metric';
            cell.innerHTML =
                '<div class="summary-label"><span class="icon" aria-hidden="true"></span><span></span></div><b class="metric-value"></b>';
            element.querySelector('.panel-summary')!.append(cell);
            this.summary.push({
                element: cell,
                label: cell.querySelector('.summary-label span:last-child')!,
                text: new MetricText(cell.querySelector('.metric-value')!),
                icon: cell.querySelector('.icon')!
            });
        }

        const rowHost = this.nodes['status-rows'];

        if (gpuStatus) {
            rowHost!.innerHTML = gpuStatusTemplate;
            this.vramTrack = rowHost!.querySelector('.gpu-vram-track');
        }

        for (const metric of pins ? [] : data.rows) {
            const fan = metric.id.startsWith('fan');
            const vram = gpuStatus && metric.id === 'vram';
            const temperature = metric.id.startsWith('temperature');
            const row = document.createElement('div');
            row.className = 'status-row';
            const rowIcon = gpuStatus ? '' : '<span class="icon" aria-hidden="true"></span>';
            const label = vram ? '' : `<span class="row-label">${rowIcon}<span class="row-label-text"></span></span>`;
            row.innerHTML = `${label}<b class="row-value"></b>`;

            if (!gpuStatus) {
                rowHost!.append(row);
            } else if (vram) {
                rowHost!.querySelector('.gpu-vram-reading')!.prepend(row);
            } else {
                const section = temperature ? '.gpu-temperatures' : fan ? '.gpu-fans' : '.gpu-clocks';
                rowHost!.querySelector(`${section} .gpu-reading-columns`)!.append(row);
            }

            this.rows.push({
                element: row,
                label: row.querySelector('.row-label-text'),
                text: new MetricText(row.querySelector('.row-value')!, gpuStatus && fan),
                icon: row.querySelector('.icon')
            });
        }

        for (let index = 0; index < (pins ? 6 : 0); index++) {
            const pin = document.createElement('div');
            pin.className = 'pin';
            pin.innerHTML =
                '<span class="headroom"></span><b class="pin-value"></b><div class="pin-track"><div class="pin-fill"></div><div class="peak-marker"></div><span class="overflow-marker" aria-label="Outside display range">!</span></div><span class="pin-label"></span><span class="psu-comparison"></span>';

            if (psu || !settings.enabled('showHeadroom')) {
                pin.querySelector('.headroom')!.remove();
            }

            if (!data.comparisonLabel) {
                pin.querySelector('.psu-comparison')!.remove();
            }

            if (!settings.enabled('showPeak')) {
                pin.querySelector('.peak-marker')!.remove();
            }

            setText(pin.querySelector('.pin-label')!, `P${index + 1}`);
            element.querySelector('.chart-pins')!.append(pin);
            this.pins.push({
                element: pin,
                text: new MetricText(pin.querySelector('.pin-value')!),
                comparison: pin.querySelector('.psu-comparison') ? new MetricText(pin.querySelector('.psu-comparison')!) : null,
                headroom: pin.querySelector('.headroom') ? new MetricText(pin.querySelector('.headroom')!) : null,
                fill: pin.querySelector('.pin-fill')!,
                peak: pin.querySelector('.peak-marker')
            });
        }

        element.querySelector('.reset-peak')?.addEventListener('click', reset);
        element.querySelector('.previous-panel')!.addEventListener('click', () => pageChanged(-1));
        element.querySelector('.next-panel')!.addEventListener('click', () => pageChanged(1));
        element
            .querySelectorAll<HTMLButtonElement>('[data-metric]')
            .forEach((button) => button.addEventListener('click', () => metricChanged(slot, button.dataset.metric!)));
    }

    render(data: PanelData): void {
        if (getComputedStyle(this.element).display === 'none') {
            return;
        }

        const customTitle = this.settings.text(`panel${this.slot}Title`).trim();
        setText(this.nodes['panel-title']!, customTitle || data.title);

        if (this.nodes['panel-source']) {
            setText(this.nodes['panel-source'], data.source);
        }

        this.usage?.set(data.gpuUsage);
        this.coreVoltage?.set(data.coreVoltage);
        const voltageBadge = this.element.querySelector<HTMLElement>('.core-voltage');
        if (voltageBadge) voltageBadge.hidden = !data.coreVoltage;

        if (this.nodes['gpu-usage']) {
            this.nodes['gpu-usage'].hidden = !data.gpuUsage;
        }

        const disconnected = !['Ready', 'Alert'].includes(data.state);
        this.nodes['status-pill']!.hidden = !disconnected;
        if (disconnected) setText(this.nodes['status-pill']!, data.state.replace(/([a-z])([A-Z])/g, '$1 $2'));
        this.element.dataset.kind = data.kind;
        this.element.dataset.tone = data.tone;
        this.element.dataset.comparison = data.comparisonLabel ? 'true' : 'false';
        this.element.dataset.voltage = String(data.voltage);
        this.element.dataset.psu = String(data.pluginId === 'msi-psu');
        this.element.dataset.plugin = data.pluginId;
        this.element.dataset.hero = String(this.settings.enabled(`panel${this.slot}Hero`));
        this.element.dataset.summary = String(data.summary.length > 0);
        this.element.dataset.peak = String(this.settings.enabled('showPeak'));
        this.writeMetrics(this.summary, data.summary);

        if (data.kind === 'pins') {
            this.renderPins(data);
        }

        if (data.kind === 'status') {
            this.writeMetrics(this.rows, data.rows);
            if (this.vramTrack) {
                const percent = data.vramUsagePercent;
                this.vramTrack.hidden = percent === null;

                if (percent !== null) {
                    const value = String(percent);
                    setVariable(this.vramTrack, '--vram-fill', `${value}%`);
                    if (this.vramTrack.getAttribute('aria-valuenow') !== value) {
                        this.vramTrack.setAttribute('aria-valuenow', value);
                    }
                } else {
                    this.vramTrack.removeAttribute('aria-valuenow');
                }
            }
        }

        const detail = ['Ready', 'Alert'].includes(data.state)
            ? ''
            : data.detail || 'Configure the backend service in the RTX Lens desktop app.';
        setText(this.nodes['panel-detail']!, detail);
        this.nodes['panel-detail']!.hidden = !detail;
        this.element
            .querySelector('.panel-icon')!
            .setAttribute('data-icon', data.kind === 'status' ? (data.pluginId === 'wireview-pro2' ? 'vtemp' : 'vcore') : 'pin');
    }

    private renderPins(data: PanelData): void {
        this.spread?.set(data.spread);

        if (this.nodes['highest-pin']) {
            setText(this.nodes['highest-pin'], data.highestPin);
        }

        if (this.nodes['lowest-pin']) {
            setText(this.nodes['lowest-pin'], data.lowestPin);
        }

        this.tickNodes.forEach((node, index) => setText(node, data.axis[index] ?? ''));

        const signature = `${data.identity}/${data.axisUnit}/${data.axis.join('/')}`;

        if (signature !== this.signature) {
            this.signature = signature;
            this.nodes['pin-chart']!.setAttribute(
                'aria-label',
                `${data.source}, six per-pin ${data.axisUnit} readings, ${data.limitLabel}, ${data.comparisonLabel}`
            );
        }

        data.pins.forEach((pin, index) => {
            const view = this.pins[index]!;
            view.text.set(pin.value);
            view.comparison?.set(pin.comparison);
            view.headroom?.set(pin.headroom);
            view.element.dataset.available = String(pin.number !== null);
            view.element.dataset.warning = String(pin.warning);
            view.element.dataset.overflow = String(pin.overflow);
            view.element.dataset.negative = String(pin.headroomValue !== null && pin.headroomValue < 0);
            setVariable(view.fill, '--fill', `${pin.fill}%`);

            if (view.peak) {
                setVariable(view.peak, '--peak', `${pin.peak ?? 0}%`);
                view.peak.hidden = pin.peak === null;
            }
        });

        this.element
            .querySelectorAll('[data-metric]')
            .forEach((button) => button.setAttribute('aria-pressed', String((button as HTMLElement).dataset.metric === data.axisUnit)));
    }

    private writeMetrics(
        views: {
            element: HTMLElement;
            label: HTMLElement | null;
            text: MetricText;
            icon: HTMLElement | null;
        }[],
        data: MetricData[]
    ): void {
        views.forEach((view, index) => {
            const metric = data[index];
            view.element.hidden = !metric;

            if (!metric) {
                return;
            }

            if (view.label) {
                setText(view.label, metric.label);
            }
            view.text.set(metric.text);
            if (view.icon) {
                view.icon.dataset.icon = metric.icon;
            }
            view.element.dataset.warning = String(metric.warning === true);
            view.element.dataset.metric = metric.id;
        });
    }
}