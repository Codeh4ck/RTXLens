import { definitions } from './definitions.ts';

export type WidgetProfiles = Record<string, Record<string, string>>;

const managed = new Set(definitions.filter(setting => !setting.host).map(setting => setting.id));
const cacheKey = 'rtxlens/widget-profiles';

export class WidgetProfileSettings {
    private profiles: WidgetProfiles = {};
    private serialized = '';

    constructor() {
        try {
            const cached = window.localStorage.getItem(cacheKey);
            if (cached) {
                this.profiles = JSON.parse(cached) as WidgetProfiles;
                this.serialized = cached;
            }
        } catch { /* Storage is optional in the iCUE web view. */ }
    }

    receive(profiles: WidgetProfiles): boolean {
        const serialized = JSON.stringify(profiles);
        if (serialized === this.serialized) return false;
        this.profiles = profiles;
        this.serialized = serialized;
        try { window.localStorage.setItem(cacheKey, serialized); } catch { /* Session settings still apply. */ }
        return true;
    }

    requestedProfile = 'Default';

    reader(host: (key: string) => unknown): (key: string) => unknown {
        const choice = String(host('widgetProfile') ?? '').trim();
        this.requestedProfile = choice || 'Default';
        if (choice.toLowerCase() === 'instance') return key => key === 'widgetProfile' ? 'Instance' : host(key);
        const selected = Object.keys(this.profiles).find(name => name.toLowerCase() === choice.toLowerCase()) ?? 'Default';
        const base = this.profiles.Default;
        const overrides = this.profiles[selected];
        return key => {
            if (key === 'widgetProfile') return selected;
            if (managed.has(key) && (base || overrides)) return overrides?.[key] ?? base?.[key];
            return host(key);
        };
    }
}
