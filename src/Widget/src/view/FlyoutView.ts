import { UpdateChecker } from '../updates/UpdateChecker.ts';
import { ReadOnlyApi } from '../transport/ReadOnlyApi.ts';
import type { ConfigurationSnapshot } from '../contracts/ConfigurationSnapshot.ts';
import type { WarningNotice } from '../warnings/WarningNotice.ts';
import { configurationRows } from '../configuration/configurationRows.ts';
import type { ConfigurationRow } from '../configuration/ConfigurationRow.ts';

export class FlyoutView {
    private root: HTMLElement;
    private changed: () => void;
    private showUpdates: () => void;
    private dialog: HTMLDialogElement | null = null;
    private body: HTMLElement | null = null;
    private controller: AbortController | null = null;
    private signature = '';
    mode: 'configuration' | 'warnings' | 'preview' | 'updates' | 'safeguard' | null = null;

    constructor(root: HTMLElement, changed: () => void, showUpdates: () => void = () => {}) {
        this.root = root;
        this.changed = changed;
        this.showUpdates = showUpdates;
    }

    get open(): boolean {
        return this.dialog !== null;
    }

    private show(mode: NonNullable<FlyoutView['mode']>, title: string): HTMLElement {
        this.close();
        this.mode = mode;

        const dialog = document.createElement('dialog');
        dialog.className = 'widget-flyout';
        dialog.dataset.mode = mode;
        dialog.setAttribute('aria-labelledby', 'flyout-title');
        dialog.innerHTML =
            '<header class="flyout-header"><h2 id="flyout-title"></h2><button type="button" class="flyout-close" aria-label="Close flyout">✕</button></header><div class="flyout-body" tabindex="0"></div>';
        dialog.querySelector('h2')!.textContent = title;
        dialog.querySelector('button')!.addEventListener('click', () => this.close());
        dialog.addEventListener('cancel', (event) => {
            event.preventDefault();
            this.close();
        });

        this.root.append(dialog);
        this.dialog = dialog;
        this.body = dialog.querySelector('.flyout-body')!;
        this.root.querySelector<HTMLElement>('.main-content')!.hidden = true;
        dialog.showModal();
        this.changed();

        return this.body;
    }

    async configuration(): Promise<void> {
        const body = this.show('configuration', 'Backend configuration');
        const updates = document.createElement('button');
        updates.type = 'button';
        updates.className = 'check-updates';
        updates.textContent = 'Check for software updates';
        updates.addEventListener('click', this.showUpdates);
        body.append(updates);

        const controller = new AbortController();
        this.controller = controller;

        const loading = document.createElement('p');
        loading.className = 'flyout-message';
        loading.textContent = 'Loading current service settings…';
        body.append(loading);

        try {
            const api = new ReadOnlyApi(59273);
            const snapshot = (await api.get('/configuration', controller.signal)) as ConfigurationSnapshot;

            if (controller.signal.aborted) {
                return;
            }

            if (
                !Number.isSafeInteger(snapshot?.revision) ||
                !snapshot.configuration?.plugins ||
                typeof snapshot.configuration.plugins !== 'object'
            ) {
                throw new Error('Unsupported service configuration response.');
            }

            loading.remove();
            this.section(body, `Sensor stream - revision ${snapshot.revision}`, configurationRows(snapshot.configuration.monitoring ?? {}));

            for (const [id, plugin] of Object.entries(snapshot.configuration.plugins)) {
                const name =
                    (
                        {
                            'asus-astral': 'ROG Astral',
                            'wireview-pro2': 'WireView Pro II',
                            'msi-psu': 'MSI MPG PSU'
                        } as Record<string, string>
                    )[id] ?? id;
                this.section(body, name, configurationRows(plugin as unknown as Record<string, unknown>));

                if (id !== 'wireview-pro2') {
                    continue;
                }

                try {
                    const device = await api.get('/plugins/wireview-pro2/device/config', controller.signal);

                    if (controller.signal.aborted) {
                        return;
                    }

                    if (!device || typeof device !== 'object' || Array.isArray(device)) {
                        throw new Error('Invalid cached device configuration.');
                    }

                    this.section(body, 'WireView Pro II device settings', configurationRows(device as Record<string, unknown>));
                } catch {
                    if (controller.signal.aborted) {
                        return;
                    }

                    this.section(body, 'WireView Pro II device settings', [{ label: 'Device configuration', value: 'Unavailable' }]);
                }
            }
        } catch (error) {
            if (controller.signal.aborted) {
                return;
            }

            loading.textContent = error instanceof Error ? error.message : 'Could not read service configuration.';
            loading.classList.add('flyout-error');

            const retry = document.createElement('button');
            retry.type = 'button';
            retry.className = 'flyout-retry';
            retry.textContent = 'Retry';
            retry.addEventListener('click', () => void this.configuration());
            body.append(retry);
        }

        if (!controller.signal.aborted) {
            this.notice(body);
        }
    }

    updates(checker: UpdateChecker, openRelease: (url: string) => boolean): void {
        const body = this.mode === 'updates' && this.body ? this.body : this.show('updates', 'Software updates');
        body.replaceChildren();
        this.section(body, 'Installed widget', [
            { label: 'Widget version', value: checker.installedVersion },
            { label: 'Last checked', value: checker.lastChecked }
        ]);

        const status = document.createElement('p');
        status.textContent = checker.status;
        body.append(status);

        const check = document.createElement('button');
        check.type = 'button';
        check.className = 'flyout-retry';
        check.textContent = checker.checking ? 'Checking...' : 'Check now';
        check.disabled = checker.checking;
        check.addEventListener('click', () => void checker.check(true));
        body.append(check);

        const version = checker.availableVersion;
        const url = checker.releaseUrl;

        if (version && url) {
            this.section(body, 'Widget update available', [
                { label: 'Installed', value: checker.installedVersion },
                { label: 'Available', value: version }
            ]);

            const actions = document.createElement('div');
            actions.className = 'update-actions';

            for (const [label, action] of [
                ['View release', () => {
                    if (!openRelease(url)) {
                        checker.status = 'The release page could not be opened. Check that the iCUE Link Provider is available.';
                        this.updates(checker, openRelease);
                    }
                }],
                ['Remind me later', () => checker.dismiss(false)],
                ['Skip this version', () => checker.dismiss(true)]
            ] as [string, () => void][]) {
                const button = document.createElement('button');
                button.type = 'button';
                button.className = 'flyout-retry';
                button.textContent = label;
                button.addEventListener('click', action);
                actions.append(button);
            }

            body.append(actions);
        }

        const note = document.createElement('p');
        note.textContent = 'Update checks run at startup and at the selected interval when enabled in iCUE. Remind me later postpones this version for 24 hours. Import the downloaded widget through iCUE.';
        body.append(note);
    }

    warnings(warnings: WarningNotice[], preview = false): void {
        this.showNotices(warnings, preview ? 'preview' : 'warnings', preview ? 'Warning preview' : 'Monitoring warnings');
    }

    safeguard(notices: WarningNotice[]): void {
        this.showNotices(notices, 'safeguard', 'Safeguard+');
    }

    private showNotices(warnings: WarningNotice[], mode: 'warnings' | 'preview' | 'safeguard', title: string): void {
        const preview = mode === 'preview';
        const signature = JSON.stringify([warnings, mode]);

        if (this.signature === signature && this.open) {
            return;
        }

        const body = this.mode === mode && this.body ? this.body : this.show(mode, title);
        const scroll = body.scrollTop;
        body.replaceChildren();

        if (preview) {
            const label = document.createElement('p');
            label.className = 'preview-notice';
            label.textContent = 'SIMULATED - telemetry and backend settings are unchanged.';
            body.append(label);
        }

        for (const warning of warnings) {
            const card = document.createElement('section');
            card.className = 'warning-card';
            card.innerHTML = '<span class="icon" aria-hidden="true"></span><div><h3></h3><p></p></div>';
            (card.querySelector('.icon')! as HTMLElement).dataset.icon = warning.icon;
            card.querySelector('h3')!.textContent = (preview && !warning.id.startsWith('preview/') ? 'LIVE - ' : '') + warning.title;
            card.querySelector('p')!.textContent = warning.detail;
            body.append(card);
        }

        this.notice(body);
        body.scrollTop = scroll;
        this.signature = signature;
    }

    private section(body: HTMLElement, title: string, rows: ConfigurationRow[]): void {
        const section = document.createElement('section');
        section.className = 'configuration-section';

        const heading = document.createElement('h3');
        heading.textContent = title;
        section.append(heading);

        for (const row of rows) {
            const element = document.createElement('div');
            element.className = 'configuration-row';

            const label = document.createElement('span');
            label.textContent = row.label;

            const value = document.createElement('b');
            value.textContent = row.value;
            element.append(label, value);
            section.append(element);
        }

        body.append(section);
    }

    private notice(body: HTMLElement): void {
        const notice = document.createElement('p');
        notice.className = 'configuration-notice';
        notice.textContent = 'Use the RTX Lens desktop app to change backend service configuration.';
        body.append(notice);
    }

    close(): void {
        this.controller?.abort();
        this.controller = null;
        this.dialog?.close();
        this.dialog?.remove();
        this.dialog = this.body = null;
        this.mode = null;
        this.signature = '';
        this.root.querySelector<HTMLElement>('.main-content')!.hidden = false;
        this.changed();
    }
}