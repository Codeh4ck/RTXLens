import type { WidgetProfiles } from '../settings/WidgetProfiles.ts';
import type { PluginSettings } from './PluginSettings.ts';

export interface WidgetWarningPreview {
    profile: string;
    scenario: string;
    issuedAt: string;
}

export interface ConfigurationSnapshot {
    revision: number;
    configuration: {
        plugins: Record<string, PluginSettings>;
        widget?: { profiles: WidgetProfiles; preview?: WidgetWarningPreview | null };
        monitoring?: Record<string, unknown>;
    };
}