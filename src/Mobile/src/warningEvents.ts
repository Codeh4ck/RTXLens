import type { IncidentRef } from './incidentProtocol.ts';
import { warningCategories, warningCategory, type WarningCategory } from './mobilePreferences.ts';
import type { SensorFrame } from './protocol';

type WarningEvent = { title: string; detail: string; simulation: boolean; category?: WarningCategory; incident?: IncidentRef };

export class WarningEvents {
  private instance: string | null = null;
  private seen = new Map<string, number>();
  private simulations = new Set<string>();
  private safeguards = new Set<string>();
  private firstFrameAt: number | null = null;

  receive(frame: SensorFrame): WarningEvent[] {
    const events: WarningEvent[] = [];

    if (frame.warningInstance !== this.instance) {
      this.instance = frame.warningInstance;

      this.seen.clear();
    }

    const activeEpisodes = new Set(frame.warnings.map(warning => warning.episodeId));

    for (const episode of this.seen.keys()) {
      if (!activeEpisodes.has(episode)) {
        this.seen.delete(episode);
      }
    }

    for (const warning of frame.warnings) {
      if (warning.phase === 'Active' && warning.notificationSequence > (this.seen.get(warning.episodeId) ?? 0)) {
        this.seen.set(warning.episodeId, warning.notificationSequence);
       
        if (frame.notificationsEnabled) {
          events.push({ title: warning.title, detail: warning.detail, simulation: false, category: warningCategory(warning.pluginId, warning.icon) });
        }
      }
    }

    const currentSimulations = new Set(frame.simulationWarnings.map(warning => warning.id));

    for (const warning of frame.simulationWarnings) { 
      if (!this.simulations.has(warning.id)) {
        events.push({ title: warning.title, detail: warning.detail, simulation: true });
      }
    }

    this.simulations = currentSimulations;

    const currentSafeguards = new Set(frame.safeguards.map(notice => notice.id));

    for (const notice of frame.safeguards) {
      if (this.firstFrameAt !== null && !notice.isSimulation && !this.safeguards.has(notice.id) && Date.parse(notice.startedAt) >= this.firstFrameAt) {
        events.push({
          title: `Safeguard+ triggered on connector ${notice.connector}`, detail: 'Open RTX Lens to view the incident.',
          simulation: false, category: warningCategories.safeguard, incident: { id: notice.id, kind: 'safeguard' },
        });
      }
    }
    
    this.safeguards = currentSafeguards;
    this.firstFrameAt ??= Date.parse(frame.timestamp);

    return events;
  }
}
