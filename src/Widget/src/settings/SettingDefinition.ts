import type { SettingOption } from './SettingOption.ts';

export interface SettingDefinition {
    id: string;
    host?: boolean;
    section?: string;
    label: string;
    group: string;
    type: 'slider' | 'switch' | 'color' | 'combobox' | 'textfield' | 'media-selector';
    defaultValue: string | number | boolean | null;
    min?: number;
    max?: number;
    step?: number;
    unit?: string;
    options?: SettingOption[];
}