import { AsusId, WireViewId } from './pluginIds.ts';
export const warningCategories = {
  connector: 1,
  temperature: 2,
  safeguard: 4,
  other: 8,
} as const;

export type WarningCategory = typeof warningCategories[keyof typeof warningCategories];
export type TemperatureUnit = 'celsius' | 'fahrenheit';
export type NotificationPreferences = { categories: number; showTitle: boolean };

export const homeCards = [
  { id: 'status', label: 'PC status' },
  { id: 'gpuUsage', label: 'GPU usage' },
  { id: 'coreTemperature', label: 'Core temperature' },
  { id: 'connectorPower', label: 'Connector power' },
  { id: 'activeWarnings', label: 'Active warnings' },
] as const;

export type HomeCardId = typeof homeCards[number]['id'];
export type HomePinPanel = { kind: 'pins'; pluginId: string; deviceId: string; connectorId: string | null };
export type HomePanel = { kind: 'metric'; id: HomeCardId } | HomePinPanel;
export type DisplayPreferences = { homePanels: HomePanel[]; hideIdleMsiConnectors: boolean; useGrouping: boolean };

export const defaultDisplayPreferences: DisplayPreferences = {
  homePanels: homeCards.map(card => ({ kind: 'metric', id: card.id })),
  hideIdleMsiConnectors: false,
  useGrouping: false,
};

export function homePanelKey(panel: HomePanel): string {
  return panel.kind === 'metric' ? panel.id : JSON.stringify([panel.pluginId, panel.deviceId, panel.connectorId]);
}

export function moveHomePanel(panels: HomePanel[], index: number, target: number): HomePanel[] {
  if (!panels[index] || !panels[target]) return panels;

  const reordered = [...panels];
  reordered.splice(target, 0, reordered.splice(index, 1)[0]);

  return reordered;
}

function readHomePanel(value: unknown): HomePanel | null {
  if (typeof value !== 'object' || value === null || !('kind' in value)) return null;

  if (value.kind === 'metric' && 'id' in value) {
    const card = homeCards.find(card => card.id === value.id);
    return card ? { kind: 'metric', id: card.id } : null;
  }

  if (value.kind === 'pins' && 'pluginId' in value && typeof value.pluginId === 'string' && 'deviceId' in value
    && typeof value.deviceId === 'string' && 'connectorId' in value && (value.connectorId === null || typeof value.connectorId === 'string')) {
    return { kind: 'pins', pluginId: value.pluginId, deviceId: value.deviceId, connectorId: value.connectorId };
  }

  return null;
}

export function decodeDisplayPreferences(value: string): DisplayPreferences {
  const parsed: unknown = JSON.parse(value);
  if (typeof parsed !== 'object' || parsed === null) throw new Error('Invalid display preferences.');

  let panels = defaultDisplayPreferences.homePanels;

  if ('homePanels' in parsed && Array.isArray(parsed.homePanels)) {
    const saved = parsed.homePanels.map(readHomePanel).filter(panel => panel !== null);
    panels = Array.from(new Map(saved.map(panel => [homePanelKey(panel), panel])).values());
  } else if ('visibleHomeCards' in parsed && Array.isArray(parsed.visibleHomeCards)) {
    const saved = parsed.visibleHomeCards;
    panels = panels.filter(panel => panel.kind === 'metric' && saved.includes(panel.id));
  }

  if (!('layoutVersion' in parsed) && !panels.some(panel => panel.kind === 'metric' && panel.id === 'status')) {
    panels = [{ kind: 'metric', id: 'status' }, ...panels];
  }

  return {
    homePanels: panels,
    useGrouping: 'useGrouping' in parsed && parsed.useGrouping === true,
    hideIdleMsiConnectors: 'hideIdleMsiConnectors' in parsed && parsed.hideIdleMsiConnectors === true,
  };
}

export const defaultNotificationPreferences: NotificationPreferences = {
  categories: warningCategories.connector | warningCategories.temperature | warningCategories.safeguard | warningCategories.other,
  showTitle: true,
};

export function decodeNotificationPreferences(value: string): NotificationPreferences {
  const parsed: unknown = JSON.parse(value);

  if (typeof parsed === 'object' && parsed !== null && 'categories' in parsed && 'showTitle' in parsed
    && typeof parsed.categories === 'number' && Number.isInteger(parsed.categories)
    && parsed.categories >= 0 && parsed.categories <= defaultNotificationPreferences.categories && typeof parsed.showTitle === 'boolean') {
    return { categories: parsed.categories, showTitle: parsed.showTitle };
  }
  
  throw new Error('Invalid notification preferences.');
}

export function warningCategory(pluginId: string, icon: string): WarningCategory {
  if (icon === 'temperature') return warningCategories.temperature;
  return pluginId === AsusId || pluginId === WireViewId ? warningCategories.connector : warningCategories.other;
}

export function allowsWarning(preferences: NotificationPreferences, category?: WarningCategory) {
  return category === undefined || (preferences.categories & category) === category;
}
