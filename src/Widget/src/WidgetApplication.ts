import { UpdateChecker } from './updates/UpdateChecker.ts';
import { UpdateStateStore } from './updates/UpdateStateStore.ts';
import { widgetVersion } from './updates/WidgetVersion.ts';
import { Settings } from './settings/Settings.ts';
import { applyAppearance } from './settings/applyAppearance.ts';
import { TelemetryStore } from './telemetry/TelemetryStore.ts';
import { PeakStore } from './telemetry/PeakStore.ts';
import { ServiceClient } from './transport/ServiceClient.ts';
import { Formatter } from './formatting/Formatter.ts';
import { resolvePanel } from './panels/resolvePanel.ts';
import { PanelPresenter } from './panels/PanelPresenter.ts';
import { PanelView } from './view/PanelView.ts';
import { setText, setVariable } from './view/setText.ts';
import { MediaViewer } from './media/MediaViewer.ts';
import { normalizeMediaConfig } from './media/normalizeMediaConfig.ts';
import { DoubleTap } from './input/DoubleTap.ts';
import { FlyoutView } from './view/FlyoutView.ts';
import { WarningPresenter } from './warnings/WarningPresenter.ts';
import { WarningTracker } from './warnings/WarningTracker.ts';
import { SafeguardNoticeTracker } from './warnings/SafeguardNoticeTracker.ts';
import type { SafeguardExecutionNotice } from './contracts/SafeguardExecutionNotice.ts';
import { previewWarnings } from './warnings/previewWarnings.ts';

export class WidgetApplication {
    private root: HTMLElement;
    private settings = new Settings();
    private store = new TelemetryStore();
    private peaks = new PeakStore();
    private format = new Formatter(this.settings);
    private presenter = new PanelPresenter(this.store, this.settings, this.format, this.peaks);
    private client = new ServiceClient(this.store, () => this.requestRender());
    private views: (PanelView | null)[] = [null, null, null];
    private viewKeys = ['', '', ''];
    private flyout: FlyoutView;
    private doubleTap: DoubleTap;
    private warnings = new WarningPresenter(this.store);
    private warningTracker = new WarningTracker();
    private safeguardTracker: SafeguardNoticeTracker;
    private safeguardShown = new Map<string, SafeguardExecutionNotice>();
    private previewRequested = false;
    private media: MediaViewer;
    private frame = 0;
    private active = 0;
    private lastPageAt = Date.now();
    private timer: ReturnType<typeof setInterval>;
    private observer: ResizeObserver;
    private nodes: Record<string, HTMLElement> = {};
    private destroyed = false;
    private updates: UpdateChecker;
    private updateTimer: ReturnType<typeof setInterval>;
    private openRelease: (url: string) => boolean;
    private updateInstance: string;
    private visibilityChanged = (): void => {
        this.requestRender();
        void this.updates.check();
    };

    constructor(root: HTMLElement, instance = 'default', openRelease: (url: string) => boolean = () => false) {
        this.root = root;
        this.nodes['service-alert'] = document.getElementById('service-alert')!;
        this.openRelease = openRelease;
        this.updateInstance = instance;
        this.flyout = new FlyoutView(root, () => this.requestRender(), () => {
            this.showUpdates();
            void this.updates.check(true);
        });

        let storage: Storage | null = null;

        try {
            storage = window.localStorage;
        } catch {
            // Some embedded web views do not permit persistent storage.
        }

        this.safeguardTracker = new SafeguardNoticeTracker('rtx-monitor-safeguard/' + instance, storage);
        this.updates = new UpdateChecker(widgetVersion(), new UpdateStateStore(`rtx-monitor-updates/${instance}`, storage),
            () => this.updatesChanged());
        this.updates.configure(false, 'Daily');
        this.updateTimer = setInterval(() => void this.updates.check(), 60000);
        this.doubleTap = new DoubleTap(root, () => {
            if (!this.flyout.open) {
                void this.flyout.configuration();
            }
        });

        root.tabIndex = 0;
        root.title = 'Double tap to view backend configuration';
        this.media = new MediaViewer(document.getElementById('media-background')!, (message) => {
            this.root.dataset.mediaError = message;
        });

        this.observer = new ResizeObserver(() => {
            this.media.applyTransform();
            this.requestRender();
        });

        this.observer.observe(root);
        document.addEventListener('visibilitychange', this.visibilityChanged);
        this.timer = setInterval(() => {
            if (document.hidden) {
                return;
            }

            if (
                !this.flyout.open &&
                this.settings.enabled('autoPage') &&
                this.capacity() < this.assigned().length &&
                Date.now() - this.lastPageAt >= this.settings.number('pageSeconds') * 1000
            ) {
                this.page(1);
            }

            this.requestRender();
        }, 500);

        window.addEventListener('pagehide', () => this.destroy(), { once: true });
    }

    setUpdateIdentity(instance: string): void {
        if (this.updateInstance === instance) {
            return;
        }

        this.updates.stop();
        this.updateInstance = instance;
        let storage: Storage | null = null;

        try {
            storage = window.localStorage;
        } catch {
            // Persistent storage is optional in the embedded host.
        }

        this.safeguardTracker = new SafeguardNoticeTracker('rtx-monitor-safeguard/' + instance, storage);
        this.updates = new UpdateChecker(widgetVersion(), new UpdateStateStore(`rtx-monitor-updates/${instance}`, storage),
            () => this.updatesChanged());
        this.updates.configure(false, 'Daily');
        this.updatesChanged();
    }

    updateSettings(read: (name: string) => unknown, defaultTemperatureUnit: string, updatesReady = true): void {
        const oldPage = this.settings.text('smallPanel');
        const oldPeak = this.settings.enabled('showPeak');
        const oldPreview = this.settings.text('warningPreview');
        this.settings.update(read);
        this.updates.configure(updatesReady && this.settings.enabled('checkUpdates'), this.settings.text('updateFrequency'));
        void this.updates.check();

        if (oldPage !== this.settings.text('smallPanel')) {
            this.active = this.settings.number('smallPanel') - 1;
        }

        if (oldPeak !== this.settings.enabled('showPeak')) {
            this.peaks.clear();
        }

        if (oldPreview !== this.settings.text('warningPreview')) {
            this.previewRequested = this.settings.text('warningPreview') !== 'none';

            if (!this.previewRequested && this.flyout.mode === 'preview') {
                this.flyout.close();
            }
        }

        applyAppearance(this.root, this.settings);
        this.format.configure(defaultTemperatureUnit);
        delete this.root.dataset.mediaError;
        this.media.loadMedia(normalizeMediaConfig(this.settings.raw('backgroundMedia')));
        this.client.start(59273);
        this.requestRender();
    }

    private showUpdates(): void {
        this.flyout.updates(this.updates, this.openRelease);
    }

    private updatesChanged(): void {
        const version = this.updates.availableVersion;

        if (version) {
            let badge = this.nodes['update-notice'];

            if (!badge) {
                badge = document.createElement('button');
                badge.className = 'update-notice';
                badge.addEventListener('click', () => this.showUpdates());
                this.root.querySelector('.main-content')!.prepend(badge);
                this.nodes['update-notice'] = badge;
            }

            setText(badge, `Widget ${version} available - view update`);
        } else {
            this.nodes['update-notice']?.remove();
            delete this.nodes['update-notice'];
        }

        if (this.flyout.mode === 'updates') {
            this.showUpdates();
        }
    }

    private assigned(): number[] {
        return [0, 1, 2].filter((index) => this.settings.text(`panel${index + 1}`) !== 'off');
    }

    private capacity(): number {
        const cssCapacity = Number(getComputedStyle(document.documentElement).getPropertyValue('--layout-capacity')) || 1;
        const configured =
            (
                {
                    One: 1,
                    Two: 2,
                    Three: 3
                } as Record<string, number>
            )[this.settings.text('panelCount')] ?? 3;

        return Math.min(cssCapacity, configured);
    }

    private page(direction: number): void {
        const assigned = this.assigned();
        const index = assigned.indexOf(this.active);
        this.active = assigned[(index + direction + assigned.length) % assigned.length] ?? 0;
        this.lastPageAt = Date.now();
        this.requestRender();
    }

    private changeMetric(slot: number, metric: string): void {
        const binding = resolvePanel(slot, this.settings, this.store, Date.now());

        if (binding.pluginId === 'msi-psu') {
            return;
        }

        this.settings.setPinMetric(binding.pluginId === 'wireview-pro2' ? 'wireview' : 'asus', metric);
        this.requestRender();
    }

    private requestRender(): void {
        if (this.destroyed || this.frame || document.hidden) {
            return;
        }

        this.frame = requestAnimationFrame(() => {
            this.frame = 0;
            this.render();
        });
    }

    private renderSafeguard(): boolean {
        const mode = this.flyout.mode;
        if (mode !== 'safeguard') {
            this.safeguardShown.clear();

            if (mode !== null && mode !== 'warnings') {
                return false;
            }
        }

        const feed = this.store.snapshot?.safeguardExecutions ?? [];
        for (const execution of this.safeguardTracker.receive(feed)) {
            this.safeguardShown.set(execution.id, execution);
        }

        if (!this.safeguardShown.size) {
            return false;
        }

        for (const execution of feed) {
            if (this.safeguardShown.has(execution.id)) {
                this.safeguardShown.set(execution.id, execution);
            }
        }

        this.flyout.safeguard([...this.safeguardShown.values()].reverse().map((execution) => ({
            id: execution.id,
            icon: 'overcurrent',
            title: 'Safeguard+ script executed',
            detail: [execution.script, 'Connector ' + execution.connector,
                new Date(execution.startedAt).toLocaleString(), execution.status].join(' · ')
        })));
        return true;
    }

    private render(): void {
        const now = Date.now();
        const notices = this.warnings.present(now);
        this.root.dataset.connection = this.store.connection;

        const warningFeed = this.store.snapshot?.warnings;
        const deliverable = warningFeed
            ? {
                  ...warningFeed,
                  states: warningFeed.states.map((state) =>
                      this.store.reporting(state.pluginId, now) ? state : { ...state, phase: 'Unavailable' as const }
                  )
              }
            : undefined;

        if (this.renderSafeguard()) {
            return;
        }

        const newWarning = this.warningTracker.update(deliverable, this.settings.enabled('showAlerts'));

        if (this.flyout.mode === 'preview' || this.previewRequested) {
            const simulated = previewWarnings(this.settings.text('warningPreview'));

            if (simulated.length) {
                this.flyout.warnings([...simulated, ...notices], true);
            }

            this.previewRequested = false;
        } else if (this.settings.enabled('showAlerts') && this.store.snapshot?.warnings?.notificationsEnabled) {
            if (newWarning || (this.flyout.mode === 'warnings' && notices.length)) {
                this.flyout.warnings(notices);
            } else if (this.flyout.mode === 'warnings') {
                this.flyout.close();
            }
        } else if (this.flyout.mode === 'warnings') {
            this.flyout.close();
        }

        // Keep the socket and alert tracking active while a flyout is open, but stop dashboard work.
        if (this.flyout.open) {
            return;
        }

        setText(this.nodes['service-alert']!, notices.map((notice) => `${notice.title}: ${notice.detail}`).join(' · '));
        this.nodes['service-alert']!.hidden = !notices.length || !this.settings.enabled('showAlerts');

        const assigned = this.assigned();

        if (!assigned.includes(this.active)) {
            this.active = assigned[0] ?? 0;
        }

        const activeIndex = assigned.indexOf(this.active);
        const visibleCount = Math.min(this.capacity(), assigned.length);

        for (let index = 0; index < this.views.length; index++) {
            const element = document.getElementById(`panel${index + 1}`)!;
            const position = (assigned.indexOf(index) - activeIndex + assigned.length) % assigned.length;
            const visible = assigned.includes(index) && position < visibleCount;
            element.hidden = !visible;
            element.dataset.position = String(position);
            setVariable(element, '--panel-order', String(position));

            if (!visible) {
                // Hidden or unconfigured panels retain neither DOM nor presenter work.
                if (this.views[index]) {
                    element.replaceChildren();
                }

                this.views[index] = null;
                this.viewKeys[index] = '';

                continue;
            }

            const binding = resolvePanel(index + 1, this.settings, this.store, now);
            const showSummary = this.settings.enabled(`panel${index + 1}Hero`);
            const data = this.presenter.present(binding, now, showSummary);
            const key = JSON.stringify([
                binding.kind,
                binding.pluginId,
                showSummary,
                Boolean(data.comparisonLabel),
                data.rows.map(row => row.id),
                ...['showSource', 'showSpread', 'showAxis', 'showHeadroom', 'showPeak'].map((name) => this.settings.enabled(name))
            ]);

            if (!this.views[index] || this.viewKeys[index] !== key) {
                this.views[index] = new PanelView(
                    element,
                    index + 1,
                    this.settings,
                    data,
                    () => {
                        this.peaks.clear();
                        this.requestRender();
                    },
                    (panel, metric) => this.changeMetric(panel, metric),
                    (direction) => this.page(direction)
                );

                this.viewKeys[index] = key;
            }

            this.views[index]!.render(data);
        }

        this.root.dataset.paged = String(assigned.length > visibleCount);
        this.root.setAttribute(
            'aria-label',
            `${this.store.connection === 'live' ? 'Telemetry connected' : this.store.connectionMessage}. Double tap or press Enter to view backend configuration.`
        );
    }

    private destroy(): void {
        this.destroyed = true;
        this.client.stop();
        clearInterval(this.timer);
        clearInterval(this.updateTimer);
        this.updates.stop();

        if (this.frame) {
            cancelAnimationFrame(this.frame);
        }

        this.observer.disconnect();
        document.removeEventListener('visibilitychange', this.visibilityChanged);
        this.media.clear();
        this.doubleTap.destroy();
        this.flyout.close();
    }
}