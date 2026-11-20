import type { UpdateCheckState } from './UpdateCheckState.ts';
import { UpdateStateStore } from './UpdateStateStore.ts';
import { isNewerVersion, nextUpdateCheck, parseReleaseManifest, releaseTagUrl, updateManifestUrl } from './versions.ts';

export class UpdateChecker {
    private readonly state: UpdateCheckState;
    private enabled = true;
    private frequency = 'Daily';
    private startup = true;
    private lastAttempt: number | null = null;
    private controller: AbortController | null = null;
    private stopped = false;
    private manuallyVisible = false;
    checking = false;
    status = 'Updates have not been checked yet.';

    readonly installedVersion: string;
    private readonly store: UpdateStateStore;
    private readonly changed: () => void;
    private readonly request: typeof fetch;
    private readonly now: () => number;

    constructor(installedVersion: string, store: UpdateStateStore, changed: () => void,
        request: typeof fetch = globalThis.fetch.bind(globalThis), now: () => number = Date.now) {
        this.installedVersion = installedVersion;
        this.store = store;
        this.changed = changed;
        this.request = request;
        this.now = now;
        this.state = store.load();
    }

    get availableVersion(): string | null {
        const manifest = this.state.manifest;

        if (!manifest || !isNewerVersion(manifest.widget, this.installedVersion)) {
            return null;
        }

        const dismissal = this.state.dismissal;

        if (!this.manuallyVisible && dismissal?.version === manifest.widget
            && (dismissal.skipped || (dismissal.remindAfter ?? 0) > this.now())) {
            return null;
        }

        return manifest.widget;
    }

    get releaseUrl(): string | null {
        return this.state.manifest?.releaseUrl ?? (this.state.manifest ? releaseTagUrl(this.state.manifest.releaseTag) : null);
    }

    get lastChecked(): string {
        return this.state.lastSuccessfulCheck === null ? 'Never' : new Date(this.state.lastSuccessfulCheck).toLocaleString();
    }

    configure(enabled: boolean, frequency: string): void {
        if (enabled !== this.enabled) {
            this.controller?.abort();
            this.startup = enabled;
        }

        this.enabled = enabled;
        this.frequency = frequency;
    }

    async check(manual = false): Promise<void> {
        if (this.stopped || this.checking || !manual && !this.enabled) {
            return;
        }

        const now = this.now();
        const last = this.state.lastSuccessfulCheck;
        const due = this.startup || last === null || last > now || nextUpdateCheck(last, this.frequency) <= now;

        if (!manual && (!due || !this.startup && this.lastAttempt !== null && now - this.lastAttempt < 600000)) {
            this.manuallyVisible = false;
            this.changed();
            return;
        }

        this.controller = new AbortController();
        const controller = this.controller;
        const timeout = setTimeout(() => controller.abort(), 10000);
        this.startup = false;
        this.lastAttempt = now;
        this.checking = true;
        this.status = 'Checking for updates...';
        this.changed();

        try {
            const response = await this.request(updateManifestUrl, { signal: controller.signal, cache: 'no-cache' });
            if (!response.ok) {
                throw new Error('Update manifest is unavailable.');
            }

            const text = await response.text();

            if (text.length > 16384) {
                throw new Error('Update manifest is too large.');
            }

            const manifest = parseReleaseManifest(JSON.parse(text));

            if (controller.signal.aborted || this.stopped) {
                throw new Error('Release manifest does not match the published release.');
            }

            this.state.manifest = manifest;
            this.state.lastSuccessfulCheck = this.now();
            this.manuallyVisible = manual;
            this.status = this.availableVersion ? 'A widget update is available.' : 'Update check complete.';
            this.store.save(this.state);
        } catch {
            if (!this.stopped) {
                this.status = controller.signal.aborted ? 'Update check cancelled or timed out.' : 'Could not check for updates. Try again later.';
            }
        } finally {
            clearTimeout(timeout);
            this.controller = null;
            this.checking = false;

            if (!this.stopped) {
                this.changed();
            }
        }
    }

    dismiss(skip: boolean): void {
        const version = this.state.manifest?.widget;

        if (!version) {
            return;
        }

        this.state.dismissal = { version, skipped: skip, remindAfter: skip ? null : this.now() + 86400000 };
        this.manuallyVisible = false;
        this.store.save(this.state);
        this.changed();
    }

    stop(): void {
        this.stopped = true;
        this.controller?.abort();
    }
}
