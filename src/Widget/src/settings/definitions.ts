import catalog from '../../../Common/Contracts/Configuration/WidgetSettings.json' with { type: 'json' };
import type { SettingDefinition } from './SettingDefinition.ts';

export const definitions: SettingDefinition[] = catalog as SettingDefinition[];
export const hostDefinitions = definitions.filter(setting => setting.host);
export const defaults = Object.fromEntries(definitions.map(setting => [setting.id, setting.defaultValue]));
